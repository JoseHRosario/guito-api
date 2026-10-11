using GuitoApi.Infrastructure.Import;
using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Model;
using GuitoApi.Services.Import;
namespace GuitoApi.Tests;

internal static class ImportHarness
{
    public static HistoricalExpenseImportService Create(FakeSheetsHttpHandler sheets, ImportDatabaseTransport database) =>
        new(new ArchivedSheetsSnapshotRepository(), new DataApiHistoricalImportRepository(database, new DataApiUnitOfWork(database, database.Context)));
    public static Task<HistoricalExpenseSnapshot> CaptureAsync(FakeSheetsHttpHandler sheets, int first, int last, string separator = ".") =>
        new SheetsSnapshotReader(new FakeGooglesheetsClientProvider(sheets)).CaptureAsync("test-sheet", "Expenses", first, last, decimalSeparator: separator);
}
