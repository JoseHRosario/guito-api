using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Infrastructure.Sheets;
using GuitoApi.Repositories;
using Microsoft.Extensions.Options;

namespace GuitoApi.Infrastructure.Sheets
{
    /// <summary>
    /// Google-Sheets-backed ICategoryRepository: reads the Config tab's category
    /// range. All Sheets specifics (spreadsheet id, range) stay here.
    /// </summary>
    public class GoogleSheetsCategoryRepository : ICategoryRepository
    {
        private readonly AppConfigurationOptions _options;
        private readonly IGooglesheetsClientProvider _googlesheetsService;

        public GoogleSheetsCategoryRepository(
            IOptions<AppConfigurationOptions> options,
            IGooglesheetsClientProvider googlesheetsService)
        {
            _options = options.Value;
            _googlesheetsService = googlesheetsService;
        }

        public async Task<IReadOnlyList<CategoryListDetail>> ListAsync(CancellationToken cancellationToken = default)
        {
            var output = new List<CategoryListDetail>();
            SheetsService service = await _googlesheetsService.GetAsync(cancellationToken);

            SpreadsheetsResource.ValuesResource.GetRequest request =
                service.Spreadsheets.Values.Get(_options.Googlesheets.SpreadsheetId, _options.Googlesheets.CategoriesRange);

            ValueRange response = await request.ExecuteAsync(cancellationToken);
            var values = response.Values;
            if (values is not { Count: > 0 })
                return output;

            foreach (var row in values)
            {
                var categoryName = row[0]?.ToString();
                if (string.IsNullOrWhiteSpace(categoryName))
                    continue;

                output.Add(new CategoryListDetail { Name = categoryName });
            }

            return output.OrderBy(c => c.Name).ToList();
        }
    }
}
