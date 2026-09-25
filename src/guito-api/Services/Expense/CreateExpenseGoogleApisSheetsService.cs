using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Input;
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

        public CreateExpenseGoogleApisSheetsService(
            IOptions<AppConfigurationOptions> options,
            IGooglesheetsService googlesheetsService,
            IUserIdentityResolver userIdentityResolver)
        {
            _options = options.Value;
            _googlesheetsService = googlesheetsService;
            _userIdentityResolver = userIdentityResolver;
        }

        public async Task CreateAsync(ExpenseCreate value, CancellationToken cancellationToken = default)
        {
            SheetsService service = await _googlesheetsService.GetAsync();
            ValueRange valueRange = new ValueRange();
            valueRange.Values = new List<IList<object>> { new List<object>
            {
                value.Date.ToString("yyyy-MM-dd"),
                "", // Year
                "", // Month
                value.Amount,
                NormalizeDescription(value.Description),
                value.Category,
                _userIdentityResolver.GetEmail()
            } };

            SpreadsheetsResource.ValuesResource.AppendRequest appendRequest =
                service.Spreadsheets.Values.Append(
                    valueRange,
                    _options.Googlesheets.SpreadsheetId,
                    _options.Googlesheets.ExpensesRange);

            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            var appendResponse = await appendRequest.ExecuteAsync(cancellationToken);

            // The append response's updated range ("...!B53:G53") carries the appended row
            // index; the Year/Month formula columns of that row still need filling in.
            var match = Regex.Match(appendResponse.Updates.UpdatedRange, @"\d+$");

            if (match.Success)
            {
                valueRange.Values = new List<IList<object>> { new List<object>
                {
                    $"=YEAR(B{match.Value})",
                    $"=MONTH(B{match.Value})",
                } };

                var updateRange = $"{_options.Googlesheets.ExpensesDateRange}{match.Value}";
                SpreadsheetsResource.ValuesResource.UpdateRequest updateRequest =
                    service.Spreadsheets.Values.Update(valueRange,
                    _options.Googlesheets.SpreadsheetId,
                    updateRange);
                updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                await updateRequest.ExecuteAsync(cancellationToken);
            }
        }

        private string NormalizeDescription(string description) =>
            CultureInfo.CurrentCulture.TextInfo.ToTitleCase(description.ToLower()).Trim();
    }
}
