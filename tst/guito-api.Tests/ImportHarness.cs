using GuitoApi.Infrastructure.Import;
using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Services.Import;
namespace GuitoApi.Tests;

internal static class ImportHarness
{
    public static HistoricalExpenseImportService Create(FakeSheetsHttpHandler sheets, ImportDatabaseTransport database) =>
        new(new ArchivedSheetsSnapshotRepository(), new DataApiHistoricalImportRepository(database, new DataApiUnitOfWork(database, database.Context)));
    public static Task<FrozenSheetsSnapshot> CaptureAsync(FakeSheetsHttpHandler sheets, int first, int last, string separator = ".") =>
        new SheetsSnapshotReader(new FakeGooglesheetsClientProvider(sheets)).CaptureAsync("test-sheet", "Expenses", first, last, decimalSeparator: separator);
    public static async Task WithArchiveAsync(FrozenSheetsSnapshot snapshot, Func<string, FrozenSheetsSnapshot, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var path = await SnapshotArchive.SaveAsync(directory, snapshot);
            await action(path, await SnapshotArchive.LoadAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
