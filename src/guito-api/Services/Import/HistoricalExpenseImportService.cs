using System.Globalization;
using GuitoApi.Model;
using GuitoApi.Repositories;
namespace GuitoApi.Services.Import;

public sealed class HistoricalExpenseImportService(IFrozenExpenseSnapshotRepository source, IHistoricalImportRepository repository) : IHistoricalExpenseImportService
{
    public async Task<HistoricalImportReport> ImportAsync(HistoricalImportRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = request.Snapshot;
        if (snapshot.FirstRow < 1 || snapshot.LastRow < snapshot.FirstRow || snapshot.Rows.Count != snapshot.LastRow - snapshot.FirstRow + 1 || snapshot.UnformattedRows?.Count != snapshot.Rows.Count)
            throw new ArgumentException("Snapshot must account for every bounded row.");
        var report = new HistoricalImportReport { SnapshotId = source.Identify(snapshot), Rows = source.ReadRows(snapshot).ToList() };
        report.SourceTotal = ExactImportTotal.Sum(report.Rows.Where(r => r.Expected != null).Select(r => r.Expected!.Amount));
        report.RetainedPreviousCount = request.Reconciliation?.RetainPreviousRows.Count ?? 0;
        try { await repository.RegisterAsync(report.SnapshotId, request, report.Rows, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { report.Exceptions.Add(exception.Message); return report; }
        foreach (var row in report.Rows.Where(r => r.Status is not ("Blank" or "NonExpense")))
            await ImportRowAsync(report.SnapshotId, row, request, cancellationToken);
        report.PersistedTotal = ExactImportTotal.Sum(report.Rows.Where(r => r.Amount != null).Select(r => decimal.Parse(r.Amount!, CultureInfo.InvariantCulture)));
        report.ExcludedSourceTotal = ExactImportTotal.Sum(report.Rows.Where(r => r.Status == "Excluded" && r.Expected != null).Select(r => r.Expected!.Amount));
        report.ImportedTotal = ExactImportTotal.Sum(report.Rows.Where(r => r.Status == "Imported" && r.Parity).Select(r => r.Expected!.Amount));
        return report;
    }

    private async Task ImportRowAsync(string snapshotId, HistoricalImportRow row, HistoricalImportRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var actual = await repository.ImportRowAsync(snapshotId, row, request, cancellationToken);
            if (actual == null) { row.Status = "Excluded"; row.Exception = null; return; }
            Compare(row, actual);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { row.Status = row.Expected == null ? "Malformed" : "Failed"; row.Exception = exception.Message; }
    }

    private static void Compare(HistoricalImportRow row, ExpenseSummary actual)
    {
        var expected = row.Expected!;
        row.ExpenseId = actual.Id; row.Currency = actual.Currency; row.Amount = actual.Amount?.ToString(CultureInfo.InvariantCulture);
        row.BookingDate = actual.BookingDate?.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        row.Description = actual.Description; row.Category = actual.Category; row.CreatorEmail = actual.CreatorEmail;
        row.CreationDate = actual.CreationDate?.ToString("O"); row.UpdateDate = actual.UpdateDate?.ToString("O");
        row.Parity = actual.Amount == expected.Amount && actual.BookingDate == expected.BookingDate && actual.Currency == "EUR"
            && actual.Description == expected.Description && actual.Category == expected.Category && actual.CreatorEmail == expected.CreatorEmail
            && actual.CreationDate != null && actual.UpdateDate == null;
        row.Status = "Imported";
        row.Exception = row.Parity ? null : "Persisted record differs from frozen source or audit contract; investigate before cutover.";
    }
}
