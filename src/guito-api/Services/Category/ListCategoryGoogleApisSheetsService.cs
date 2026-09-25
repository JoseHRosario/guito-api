using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Output;
using Microsoft.Extensions.Options;

namespace GuitoApi.Services.Category
{
    public class ListCategoryGoogleApisSheetsService : IListCategoryService
    {
        private readonly AppConfigurationOptions _options;
        private readonly IGooglesheetsService _googlesheetsService;
        private readonly ILogger<ListCategoryGoogleApisSheetsService> _logger;

        public ListCategoryGoogleApisSheetsService(
            IOptions<AppConfigurationOptions> options,
            IGooglesheetsService googlesheetsService,
            ILogger<ListCategoryGoogleApisSheetsService> logger
            )
        {
            _options = options.Value;
            _googlesheetsService = googlesheetsService;
            _logger = logger;
        }

        public async Task<CategoryList> ListAsync(CancellationToken cancellationToken = default)
        {
            var output = new CategoryList();
            SheetsService service = await _googlesheetsService.GetAsync();

            // Read values from the specified range
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

                output.Categories.Add(new CategoryListDetail { Name = categoryName });
            }
            output.Categories = output.Categories.OrderBy(c => c.Name).ToList();

            return output;
        }
    }
}
