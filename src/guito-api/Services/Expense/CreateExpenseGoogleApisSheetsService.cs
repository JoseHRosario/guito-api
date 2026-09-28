using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GuitoApi.Services.Expense
{
    public class CreateExpenseGoogleApisSheetsService : ICreateExpenseService
    {
        private readonly AppConfigurationOptions _options;
        private readonly IGooglesheetsService _googlesheetsService;
        private readonly IUserIdentityResolver _userIdentityResolver;
        private readonly ISheetScopeResolver _sheetScopeResolver;

        public CreateExpenseGoogleApisSheetsService(
            IOptions<AppConfigurationOptions> options,
            IGooglesheetsService googlesheetsService,
            IUserIdentityResolver userIdentityResolver,
            ISheetScopeResolver sheetScopeResolver)
        {
            _options = options.Value;
            _googlesheetsService = googlesheetsService;
            _userIdentityResolver = userIdentityResolver;
            _sheetScopeResolver = sheetScopeResolver;
        }

        public async Task<ExpenseCreated> CreateAsync(ExpenseCreate value, CancellationToken cancellationToken = default)
        {
            SheetsService service = await _googlesheetsService.GetAsync(cancellationToken);

            ValueRange valueRange = new ValueRange
            {
                Values = new List<IList<object>>
                {
                    new List<object>
                    {
                        value.Date.ToString("yyyy-MM-dd"),
                        "", // Year
                        "", // Month
                        value.Amount,
                        NormalizeDescription(value.Description),
                        value.Category,
                        _userIdentityResolver.GetEmail()
                    }
                }
            };

            var expensesRange = _sheetScopeResolver.ExpensesRange;
            SpreadsheetsResource.ValuesResource.AppendRequest appendRequest =
                service.Spreadsheets.Values.Append(
                    valueRange,
                    _options.Googlesheets.SpreadsheetId,
                    expensesRange);

            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            var appendResponse = await appendRequest.ExecuteAsync(cancellationToken);

            // The append response's updated range ("...!B53:G53") carries the appended row
            // index; the Year/Month formula columns of that row still need filling in.
            var match = Regex.Match(appendResponse.Updates.UpdatedRange, @"\d+$");
            if (!match.Success)
                throw new ProblemException(500, "Could not resolve the appended expense row");

            var appendedRowIndex = int.Parse(match.Value);

            valueRange.Values = new List<IList<object>>
            {
                new List<object>
                {
                    $"=YEAR(B{appendedRowIndex})",
                    $"=MONTH(B{appendedRowIndex})",
                }
            };

            var updateRange = $"{_sheetScopeResolver.ExpensesDateRange}{appendedRowIndex}";
            SpreadsheetsResource.ValuesResource.UpdateRequest updateRequest =
                service.Spreadsheets.Values.Update(valueRange,
                _options.Googlesheets.SpreadsheetId,
                updateRange);
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            await updateRequest.ExecuteAsync(cancellationToken);

            return new ExpenseCreated { Id = appendedRowIndex };
        }

        private static string NormalizeDescription(string description) =>
            CultureInfo.CurrentCulture.TextInfo.ToTitleCase(description.ToLower()).Trim();
    }
}
