using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using GuitoApi.Configuration;
using GuitoApi.Model;
using Microsoft.Extensions.Options;

namespace GuitoApi.Services
{
    public class CategoryGoogleApisSheetsService : ICategoryService
    {
        private readonly AppConfigurationOptions _options;
        private readonly IGooglesheetsService _googlesheetsService;

        public CategoryGoogleApisSheetsService(
            IOptions<AppConfigurationOptions> options,
            IGooglesheetsService googlesheetsService
            )
        {
            _options = options.Value;
            _googlesheetsService = googlesheetsService;
        }

        public async Task<List<Category>> List()
        {
            var categories = new List<Category>();
            SheetsService service = _googlesheetsService.Get();

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
                    categories.Add(new Category { Name = row[0].ToString() });
#pragma warning restore CS8601 // Possible null reference assignment.
                }
            }
            return categories.OrderBy(c => c.Name).ToList();
        }
    }
}
