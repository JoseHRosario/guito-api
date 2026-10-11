using GuitoApi.Model;
using GuitoApi.Repositories;
namespace GuitoApi.Infrastructure.Import;

public sealed class ArchivedSheetsSnapshotRepository : IFrozenExpenseSnapshotRepository
{
    public string Identify(HistoricalExpenseSnapshot snapshot) => SnapshotArchive.Identity(snapshot);
    public IReadOnlyList<HistoricalImportRow> ReadRows(HistoricalExpenseSnapshot snapshot) =>
        Enumerable.Range(0, snapshot.Rows.Count).Select(i => HistoricalRowParser.Parse(snapshot, i)).ToList();
}
