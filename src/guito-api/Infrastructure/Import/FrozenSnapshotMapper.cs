using GuitoApi.Model;
using GuitoApi.Services.Import;
namespace GuitoApi.Infrastructure.Import;

public static class FrozenSnapshotMapper
{
    public static void Validate(FrozenSheetsSnapshot snapshot)
    {
        if (!HistoricalSnapshotValidator.HasCompleteBounds(snapshot.FirstRow, snapshot.LastRow, snapshot.Rows.Count) ||
            snapshot.UnformattedRows?.Count != snapshot.Rows.Count)
            throw new InvalidDataException("Snapshot does not account for its complete bounded range.");
    }

    public static HistoricalExpenseSnapshot ToDomain(this FrozenSheetsSnapshot snapshot)
    {
        Validate(snapshot);
        return new(SnapshotArchive.Identity(snapshot), snapshot.SpreadsheetId, snapshot.Tab,
            snapshot.FirstRow, snapshot.LastRow, snapshot.Currency, snapshot.DecimalSeparator,
            snapshot.CapturedAt, Enumerable.Range(0, snapshot.Rows.Count)
                .Select(i => HistoricalRowParser.Parse(snapshot, i)).ToList()) { DateFormat = snapshot.DateFormat };
    }
}
