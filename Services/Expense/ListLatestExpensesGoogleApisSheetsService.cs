using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Output;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GuitoApi.Services.Expense
{
    public class ListLatestExpensesGoogleApisSheetsService : IListLatestExpensesService
    {
        private readonly AppConfigurationOptions _options;
        private readonly IGooglesheetsService _googlesheetsService;

        public ListLatestExpensesGoogleApisSheetsService(
            IOptions<AppConfigurationOptions> options,
            IGooglesheetsService googlesheetsService)
        {
            _options = options.Value;
            _googlesheetsService = googlesheetsService;
        }

        public async Task<ExpenseListLatest> ListLatest(int count)
        {
            var output = new ExpenseListLatest();
            SheetsService service = await _googlesheetsService.Get();

            var rowIndexFromRange = GetRowIndexFromRange();
            var lastRowIndex = await GetLatestRowIndex(service);
            lastRowIndex = lastRowIndex < rowIndexFromRange ? rowIndexFromRange : lastRowIndex;

            if (lastRowIndex != null)
            {
                output = await ListLatestExpenses(service, count, lastRowIndex, rowIndexFromRange);
            }

            return output;
        }

        private async Task<ExpenseListLatest> ListLatestExpenses(SheetsService service, int count, int? lastRowIndex, int rowIndexFromRange)
        {
            var output = new ExpenseListLatest();

            var firstRowIndex = lastRowIndex - count + 1;
            firstRowIndex = firstRowIndex < rowIndexFromRange ? rowIndexFromRange : firstRowIndex;
            var range = string.Format(_options.Googlesheets.ExpensesLatestRange, firstRowIndex, lastRowIndex);
            // Read values from the specified range
            SpreadsheetsResource.ValuesResource.GetRequest request =
                service.Spreadsheets.Values.Get(_options.Googlesheets.SpreadsheetId, range);

            ValueRange response = await request.ExecuteAsync();
            IList<IList<object>> values = response.Values;

            // Print the read values
            if (values != null && values.Count > 0)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(values[i]?[0]?.ToString()))
                        continue;

                    output.Expenses.Add(new ExpenseListLatestDetail
                    {
                        StoredOrder = i + 1,
                        Date = ParseDate(values[i]?[0]),
                        Amount = ParseDecimal(values[i]?[3]),
                        Description = values[i]?[4]?.ToString(),
                        Category = values[i]?[5]?.ToString(),
                        CreatorEmail = values[i].Count > 6 ? values[i]?[6]?.ToString() : string.Empty
                    });
                }
            }
            return output;
        }

        private DateTime? ParseDate(object? value)
        {
            if (value == null)
                return null;

            if (DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, out DateTime result))
            {
                return result;
            }
            return null;
        }

        private decimal? ParseDecimal(object? value)
        {
            if (value == null)
                return null;
#pragma warning disable CS8602
            var valueString = value.ToString().Replace("€", "");
#pragma warning restore CS8602
            if (Decimal.TryParse(valueString, out decimal result))
            {
                return result;
            }
            return null;
        }

        /// <summary>
        /// Append dummy row to get the latest row index
        /// This values is returned by the API response
        /// </summary>
        /// <param name="service">Googl API Service</param>
        /// <returns>row index</returns>
        private async Task<int?> GetLatestRowIndex(SheetsService service)
        {
            var valueRange = new ValueRange { Values = new List<IList<object>> { new List<object> { "" } } };

            SpreadsheetsResource.ValuesResource.AppendRequest appendRequest =
                service.Spreadsheets.Values.Append(
                    valueRange,
                    _options.Googlesheets.SpreadsheetId,
                    _options.Googlesheets.ExpensesRange);

            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            var appendResponse = await appendRequest.ExecuteAsync();

            // Update Expense Year and Month
            // "ExpensesAux!B53:G53" = "53" - > Return the appended row index
            var match = Regex.Match(appendResponse.Updates.UpdatedRange, @"\d+$");
            return match.Success ? int.Parse(match.Value) - 1 : null;
        }

        private int GetRowIndexFromRange()
        {
            // "ExpensesAux!B5" = "5" - > Return the row index
            var match = Regex.Match(_options.Googlesheets.ExpensesRange, @"\d+$");
            return match.Success ? int.Parse(match.Value) : 0;
        }
    }
}
