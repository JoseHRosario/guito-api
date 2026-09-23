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

        public async Task<CategoryList> List()
        {
            var output = new CategoryList();
            SheetsService service = await _googlesheetsService.Get();

            // Read values from the specified range
            SpreadsheetsResource.ValuesResource.GetRequest request =
                service.Spreadsheets.Values.Get(_options.Googlesheets.SpreadsheetId, _options.Googlesheets.CategoriesRange);

            ValueRange response = await request.ExecuteAsync();
            IList<IList<object>> values = response.Values;

            // Print the read values
            if (values != null && values.Count > 0)
            {
                foreach (var row in values)
                {
                    if (string.IsNullOrWhiteSpace(row?[0]?.ToString()))
                        continue;

#pragma warning disable CS8601 // Possible null reference assignment.
                    output.Categories.Add(new CategoryListDetail { Name = row[0].ToString() });
#pragma warning restore CS8601 // Possible null reference assignment.
                }
                output.Categories = output.Categories.OrderBy(c => c.Name).ToList();
            }
            return output;
        }
    }
}
