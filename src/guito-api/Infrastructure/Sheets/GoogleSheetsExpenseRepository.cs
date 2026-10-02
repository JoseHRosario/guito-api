using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.Sheets;
using GuitoApi.Repositories;
using GuitoApi.Services;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GuitoApi.Infrastructure.Sheets
{
    /// <summary>
    /// Google-Sheets-backed IExpenseRepository. Every Sheets-specific behavior
    /// lives here and nowhere else: spreadsheet id, resolved ranges (including
    /// the Smoke-scope header via ISheetScopeResolver), the row-index↔Id
    /// mapping, and the Year/Month formula backfill on create. A future
    /// Postgres implementation replaces this class, not its callers.
    /// </summary>
    public class GoogleSheetsExpenseRepository : IExpenseRepository
    {
        private readonly AppConfigurationOptions _options;
        private readonly IGooglesheetsClientProvider _googlesheetsService;
        private readonly IUserIdentityResolver _userIdentityResolver;
        private readonly ISheetScopeResolver _sheetScopeResolver;

        public GoogleSheetsExpenseRepository(
            IOptions<AppConfigurationOptions> options,
            IGooglesheetsClientProvider googlesheetsService,
            IUserIdentityResolver userIdentityResolver,
            ISheetScopeResolver sheetScopeResolver)
        {
            _options = options.Value;
            _googlesheetsService = googlesheetsService;
            _userIdentityResolver = userIdentityResolver;
            _sheetScopeResolver = sheetScopeResolver;
        }

        public async Task<string> CreateAsync(ExpenseCreate value, CancellationToken cancellationToken = default)
        {
            SheetsService service = await _googlesheetsService.GetAsync(cancellationToken);

            var appendResponse = await AppendExpenseRowAsync(service, value, cancellationToken);
            var appendedRowIndex = ResolveAppendedRowIndex(appendResponse);
            await BackfillYearMonthFormulasAsync(service, appendedRowIndex, cancellationToken);

            return appendedRowIndex.ToString(CultureInfo.InvariantCulture);
        }

        private async Task<AppendValuesResponse> AppendExpenseRowAsync(SheetsService service,
            ExpenseCreate value, CancellationToken cancellationToken)
        {
            ValueRange valueRange = new ValueRange
            {
                Values = new List<IList<object>>
                {
                    BuildExpenseRow(value)
                }
            };

            SpreadsheetsResource.ValuesResource.AppendRequest appendRequest =
                service.Spreadsheets.Values.Append(
                    valueRange,
                    _options.Googlesheets.SpreadsheetId,
                    _sheetScopeResolver.ExpensesRange);

            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            return await appendRequest.ExecuteAsync(cancellationToken);
        }

        private IList<object> BuildExpenseRow(ExpenseCreate value) => new List<object>
        {
            value.Date.ToString("yyyy-MM-dd"),
            "", // Year
            "", // Month
            value.Amount,
            NormalizeDescription(value.Description),
            value.Category,
            _userIdentityResolver.GetEmail()
        };

        // The append response's updated range ("...!B53:G53") carries the appended
        // row index; the Year/Month formula columns of that row still need filling.
        private static int ResolveAppendedRowIndex(AppendValuesResponse appendResponse)
        {
            var match = Regex.Match(appendResponse.Updates.UpdatedRange, @"\d+$");
            return match.Success
                ? int.Parse(match.Value)
                : throw new ProblemException(500, "Could not resolve the appended expense row");
        }

        private async Task BackfillYearMonthFormulasAsync(SheetsService service, int appendedRowIndex,
            CancellationToken cancellationToken)
        {
            var valueRange = new ValueRange
            {
                Values = new List<IList<object>>
                {
                    new List<object>
                    {
                        $"=YEAR(B{appendedRowIndex})",
                        $"=MONTH(B{appendedRowIndex})",
                    }
                }
            };

            var updateRange = $"{_sheetScopeResolver.ExpensesDateRange}{appendedRowIndex}";
            SpreadsheetsResource.ValuesResource.UpdateRequest updateRequest =
                service.Spreadsheets.Values.Update(valueRange,
                _options.Googlesheets.SpreadsheetId,
                updateRange);
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            await updateRequest.ExecuteAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<ExpenseListLatestDetail>> ListLatestAsync(int limit, CancellationToken cancellationToken = default)
        {
            SheetsService service = await _googlesheetsService.GetAsync(cancellationToken);

            var (_, firstDataRow, _) = ParseAnchor(_sheetScopeResolver.ExpensesRange);
            var lastRowIndex = await GetLatestRowIndexAsync(service, cancellationToken);
            lastRowIndex = lastRowIndex < firstDataRow ? firstDataRow : lastRowIndex;
            if (lastRowIndex is null)
                return [];

            return await ReadLatestExpensesAsync(service, limit, lastRowIndex.Value, firstDataRow, cancellationToken);
        }

        private async Task<IReadOnlyList<ExpenseListLatestDetail>> ReadLatestExpensesAsync(SheetsService service,
            int limit, int lastRowIndex, int firstDataRow, CancellationToken cancellationToken)
        {
            var firstRowIndex = Math.Max(lastRowIndex - limit + 1, firstDataRow);
            // ExpensesLatestRange is a config-provided format template ("...!B{0}:H{1}").
            var range = string.Format(_sheetScopeResolver.ExpensesLatestRange, firstRowIndex, lastRowIndex);
            SpreadsheetsResource.ValuesResource.GetRequest request =
                service.Spreadsheets.Values.Get(_options.Googlesheets.SpreadsheetId, range);

            ValueRange response = await request.ExecuteAsync(cancellationToken);
            return MapExpenseRows(response.Values);
        }

        private static IReadOnlyList<ExpenseListLatestDetail> MapExpenseRows(IList<IList<object>>? values)
        {
            if (values is not { Count: > 0 })
                return [];

            var output = new List<ExpenseListLatestDetail>();
            for (var i = 0; i < values.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(values[i]?[0]?.ToString()))
                    continue;

                output.Add(new ExpenseListLatestDetail
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

        public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
        {
            // The Smoke-scoped delete per ADR-0009. Operates ONLY on the
            // environment's Smoke Test tab — the route /Smoke/{id} is the scope
            // declaration — so this implementation resolves the configured smoke
            // anchor and never consults the request scope header. Verify-then-
            // delete (reads the row back before deleting the dimension) makes it
            // idempotent on retry.
            var appendedRowIndex = ParseId(id);
            var (tabTitle, firstDataRow, _) = ParseAnchor(_options.Googlesheets.ExpensesSmokeRange);
            if (appendedRowIndex < firstDataRow)
                throw new ExpenseNotFoundException(id);

            var spreadsheetId = _options.Googlesheets.SpreadsheetId;
            SheetsService service = await _googlesheetsService.GetAsync(cancellationToken);
            var sheetId = await ResolveSheetIdAsync(service, spreadsheetId, tabTitle, cancellationToken);

            if (!await RowExistsAsync(service, spreadsheetId, tabTitle, appendedRowIndex, cancellationToken))
                throw new ExpenseNotFoundException(id);

            await DeleteRowDimensionAsync(service, spreadsheetId, sheetId, appendedRowIndex, cancellationToken);
        }

        private static int ParseId(string id)
        {
            return int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var row)
                ? row
                : throw new ExpenseNotFoundException(id);
        }

        private static string NormalizeDescription(string description) =>
            CultureInfo.CurrentCulture.TextInfo.ToTitleCase(description.ToLower()).Trim();

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

        private static async Task<int> ResolveSheetIdAsync(SheetsService service, string spreadsheetId,
            string tabTitle, CancellationToken cancellationToken)
        {
            var spreadsheet = await service.Spreadsheets.Get(spreadsheetId).ExecuteAsync(cancellationToken);
            return spreadsheet.Sheets?.FirstOrDefault(s => s.Properties?.Title == tabTitle)?.Properties?.SheetId
                ?? throw new ProblemException(500, $"Sheet tab '{tabTitle}' not found in the spreadsheet");
        }

        private static async Task<bool> RowExistsAsync(SheetsService service, string spreadsheetId,
            string tabTitle, int id, CancellationToken cancellationToken)
        {
            var response = await service.Spreadsheets.Values
                .Get(spreadsheetId, $"{tabTitle}!B{id}")
                .ExecuteAsync(cancellationToken);
            return response.Values is { Count: > 0 } && !string.IsNullOrWhiteSpace(response.Values[0]?[0]?.ToString());
        }

        private static async Task DeleteRowDimensionAsync(SheetsService service, string spreadsheetId,
            int sheetId, int id, CancellationToken cancellationToken)
        {
            var request = BuildDeleteOneRowRequest(sheetId, id);
            await service.Spreadsheets.BatchUpdate(request, spreadsheetId).ExecuteAsync(cancellationToken);
        }

        private static BatchUpdateSpreadsheetRequest BuildDeleteOneRowRequest(int sheetId, int id) => new()
        {
            Requests = new List<Request>
            {
                new Request
                {
                    DeleteDimension = new DeleteDimensionRequest
                    {
                        Range = new DimensionRange
                        {
                            SheetId = sheetId,
                            Dimension = "ROWS",
                            StartIndex = id - 1,
                            EndIndex = id,
                        }
                    }
                }
            }
        };

        private static (string TabTitle, int FirstDataRow, string DateColumn) ParseAnchor(string range)
        {
            var match = Regex.Match(range, @"^(?<tab>.*)!([A-Z]+)(?<row>\d+)$");
            if (!match.Success)
                throw new ProblemException(500, $"Invalid expense range anchor '{range}'");

            return (match.Groups["tab"].Value, int.Parse(match.Groups["row"].Value), match.Groups[1].Value);
        }
    }
}
