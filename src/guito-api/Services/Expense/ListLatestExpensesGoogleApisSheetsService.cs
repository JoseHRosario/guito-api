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

        public async Task<ExpenseListLatest> ListLatestAsync(int count, CancellationToken cancellationToken = default)
        {
            var output = new ExpenseListLatest();
            SheetsService service = await _googlesheetsService.GetAsync();

            var rowIndexFromRange = GetRowIndexFromRange();
            var lastRowIndex = await GetLatestRowIndexAsync(service, cancellationToken);
            lastRowIndex = lastRowIndex < rowIndexFromRange ? rowIndexFromRange : lastRowIndex;

            if (lastRowIndex is null)
                return output;

            return await ListLatestExpensesAsync(service, count, lastRowIndex, rowIndexFromRange, cancellationToken);
        }

        private async Task<ExpenseListLatest> ListLatestExpensesAsync(SheetsService service, int count, int? lastRowIndex,
            int rowIndexFromRange, CancellationToken cancellationToken)
        {
            var output = new ExpenseListLatest();

            var firstRowIndex = lastRowIndex - count + 1;
            firstRowIndex = firstRowIndex < rowIndexFromRange ? rowIndexFromRange : firstRowIndex;
            // ExpensesLatestRange is a config-provided format template ("...!B{0}:H{1}").
            var range = string.Format(_options.Googlesheets.ExpensesLatestRange, firstRowIndex, lastRowIndex);
            SpreadsheetsResource.ValuesResource.GetRequest request =
                service.Spreadsheets.Values.Get(_options.Googlesheets.SpreadsheetId, range);

            ValueRange response = await request.ExecuteAsync(cancellationToken);
            var values = response.Values;
            if (values is not { Count: > 0 })
                return output;

            for (var i = 0; i < values.Count; i++)
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

            return output;
        }

        private static DateTime? ParseDate(object? value)
        {
            if (value is null)
                return null;

            return DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var result)
                ? result
                : null;
        }

        private static decimal? ParseDecimal(object? value)
        {
            if (value is null)
                return null;

            var valueString = value.ToString()?.Replace("€", string.Empty);
            return decimal.TryParse(valueString, out var result) ? result : null;
        }

        // Appends a dummy row to get the sheet's next row index back from the append
        // response; the minus one maps the appended row to the last existing expense row.
        private async Task<int?> GetLatestRowIndexAsync(SheetsService service, CancellationToken cancellationToken)
        {
            var valueRange = new ValueRange { Values = new List<IList<object>> { new List<object> { "" } } };

            SpreadsheetsResource.ValuesResource.AppendRequest appendRequest =
                service.Spreadsheets.Values.Append(
                    valueRange,
                    _options.Googlesheets.SpreadsheetId,
                    _options.Googlesheets.ExpensesRange);

            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            var appendResponse = await appendRequest.ExecuteAsync(cancellationToken);

            // The append response's updated range ("...!B53:G53") carries the appended
            // row index; minus one maps it to the last existing expense row.
            var match = Regex.Match(appendResponse.Updates.UpdatedRange, @"\d+$");
            return match.Success ? int.Parse(match.Value) - 1 : null;
        }

        private int GetRowIndexFromRange()
        {
            // The first data row: "ExpensesAux!B5" → row index 5.
            var match = Regex.Match(_options.Googlesheets.ExpensesRange, @"\d+$");
            return match.Success ? int.Parse(match.Value) : 0;
        }
    }
}
