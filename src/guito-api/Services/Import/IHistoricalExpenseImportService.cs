using GuitoApi.Model;
namespace GuitoApi.Services.Import;
public interface IHistoricalExpenseImportService
{
    Task<HistoricalImportReport> ImportAsync(HistoricalImportRequest request, CancellationToken cancellationToken = default);
}
