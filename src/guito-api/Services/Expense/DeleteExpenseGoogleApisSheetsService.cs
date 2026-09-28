using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using GuitoApi.Configuration;
using GuitoApi.Exceptions;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace GuitoApi.Services.Expense
{
    /// <summary>
    /// Id-scoped delete per ADR-0009. Operates ONLY on the environment's Smoke Test
    /// tab: the route /Smoke/{id} is the scope declaration, so this service resolves
    /// ranges from the configured smoke anchor and never consults the request scope
    /// header — it cannot be pointed at a real tab. Verify-then-delete (reads the
    /// row back before deleting the dimension) and returns 404 when the id is
    /// already gone, making it idempotent on retry.
    /// </summary>
    public class DeleteExpenseGoogleApisSheetsService : IDeleteExpenseRowService
    {
        private readonly AppConfigurationOptions _options;
        private readonly IGooglesheetsService _googlesheetsService;

        public DeleteExpenseGoogleApisSheetsService(
            IOptions<AppConfigurationOptions> options,
            IGooglesheetsService googlesheetsService)
        {
            _options = options.Value;
            _googlesheetsService = googlesheetsService;
        }

        public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
        {
            var (tabTitle, firstDataRow) = ParseAnchor(_options.Googlesheets.ExpensesSmokeRange);
            if (id < firstDataRow)
                throw new ProblemException(404, $"Expense {id} not found");

            var spreadsheetId = _options.Googlesheets.SpreadsheetId;
            SheetsService service = await _googlesheetsService.GetAsync(cancellationToken);
            var sheetId = await ResolveSheetIdAsync(service, spreadsheetId, tabTitle, cancellationToken);

            if (!await RowExistsAsync(service, spreadsheetId, tabTitle, id, cancellationToken))
                throw new ProblemException(404, $"Expense {id} not found");

            await DeleteRowDimensionAsync(service, spreadsheetId, sheetId, id, cancellationToken);
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
            var request = new BatchUpdateSpreadsheetRequest
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

            await service.Spreadsheets.BatchUpdate(request, spreadsheetId).ExecuteAsync(cancellationToken);
        }

        private static (string TabTitle, int FirstDataRow) ParseAnchor(string range)
        {
            var match = Regex.Match(range, @"^(?<tab>.*)!([A-Z]+)(?<row>\d+)$");
            if (!match.Success)
                throw new ProblemException(500, $"Invalid smoke range anchor '{range}'");

            return (match.Groups["tab"].Value, int.Parse(match.Groups["row"].Value));
        }
    }
}
