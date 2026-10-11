using GuitoApi.Model;
using GuitoApi.Repositories;
namespace GuitoApi.Infrastructure.Import;

public sealed class ArchivedSheetsSnapshotRepository : IFrozenExpenseSnapshotRepository
{
    public string Identify(HistoricalExpenseSnapshot snapshot) => snapshot.SnapshotId;
    public IReadOnlyList<HistoricalImportRow> ReadRows(HistoricalExpenseSnapshot snapshot) =>
        snapshot.Rows.Select(row => row with { }).ToList();
}
