using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GuitoApi.Services.Expense
{
    public class ListLatestExpensesGoogleApisSheetsService : IListLatestExpensesService
    {
        private readonly AppConfigurationOptions _options;
        private readonly IGooglesheetsService _googlesheetsService;
        private readonly ISheetScopeResolver _sheetScopeResolver;

        public ListLatestExpensesGoogleApisSheetsService(
            IOptions<AppConfigurationOptions> options,
            IGooglesheetsService googlesheetsService,
            ISheetScopeResolver sheetScopeResolver)
        {
            _options = options.Value;
            _googlesheetsService = googlesheetsService;
            _sheetScopeResolver = sheetScopeResolver;
        }

        public async Task<ExpenseListLatest> ListLatestAsync(int count, CancellationToken cancellationToken = default)
        {
            var output = new ExpenseListLatest();
            SheetsService service = await _googlesheetsService.GetAsync(cancellationToken);

            var (_, firstDataRow, _) = ParseAnchor(_sheetScopeResolver.ExpensesRange);
            var lastRowIndex = await GetLatestRowIndexAsync(service, cancellationToken);
            lastRowIndex = lastRowIndex < firstDataRow ? firstDataRow : lastRowIndex;

            if (lastRowIndex is null)
                return output;

            return await ListLatestExpensesAsync(service, count, lastRowIndex.Value, firstDataRow, cancellationToken);
        }

        private async Task<ExpenseListLatest> ListLatestExpensesAsync(SheetsService service, int count, int lastRowIndex,
            int firstDataRow, CancellationToken cancellationToken)
        {
            var output = new ExpenseListLatest();

            var firstRowIndex = lastRowIndex - count + 1;
            firstRowIndex = firstRowIndex < firstDataRow ? firstDataRow : firstRowIndex;
            // ExpensesLatestRange is a config-provided format template ("...!B{0}:H{1}").
            var range = string.Format(_sheetScopeResolver.ExpensesLatestRange, firstRowIndex, lastRowIndex);
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

        // Pure read of the date column (ADR 0009): finds the last data row by reading
        // the first column of the scoped range from its anchor downward. Replaces the
        // old append-a-dummy-row probe, which mutated the datastore on every read.
        private async Task<int?> GetLatestRowIndexAsync(SheetsService service, CancellationToken cancellationToken)
        {
            var (tabTitle, firstDataRow, dateColumn) = ParseAnchor(_sheetScopeResolver.ExpensesRange);
            var range = $"{tabTitle}!{dateColumn}{firstDataRow}:{dateColumn}";
            SpreadsheetsResource.ValuesResource.GetRequest request =
                service.Spreadsheets.Values.Get(_options.Googlesheets.SpreadsheetId, range);

            ValueRange response = await request.ExecuteAsync(cancellationToken);
            return response.Values is { Count: > 0 }
                ? firstDataRow + response.Values.Count - 1
                : null;
        }

        private static (string TabTitle, int FirstDataRow, string DateColumn) ParseAnchor(string range)
        {
            var match = Regex.Match(range, @"^(?<tab>.*)!([A-Z]+)(?<row>\d+)$");
            if (!match.Success)
                throw new ProblemException(500, $"Invalid expense range anchor '{range}'");

            return (match.Groups["tab"].Value, int.Parse(match.Groups["row"].Value), match.Groups[1].Value);
        }
    }
}
