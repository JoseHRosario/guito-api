using GuitoApi.Model;
namespace GuitoApi.Repositories;

public interface IHistoricalImportRepository
{
    Task RegisterAsync(string snapshotId, HistoricalImportRequest request, IReadOnlyList<HistoricalImportRow> rows, CancellationToken cancellationToken);
    Task<ExpenseSummary?> ImportRowAsync(string snapshotId, HistoricalImportRow row, HistoricalImportRequest request, CancellationToken cancellationToken);
}
