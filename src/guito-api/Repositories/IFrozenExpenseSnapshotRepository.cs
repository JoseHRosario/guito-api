using GuitoApi.Model;
namespace GuitoApi.Repositories;

public interface IFrozenExpenseSnapshotRepository
{
    string Identify(HistoricalExpenseSnapshot snapshot);
    IReadOnlyList<HistoricalImportRow> ReadRows(HistoricalExpenseSnapshot snapshot);
}
