using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using GuitoApi.Configuration;
using GuitoApi.Model;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace GuitoApi.Services
{
    public class ExpenseGoogleApisSheetsService : IExpenseService
    {
        private readonly AppConfigurationOptions _options;
        private readonly IGooglesheetsService _googlesheetsService;
        private readonly IUserIdentityResolver _userIdentityResolver;

        public ExpenseGoogleApisSheetsService(
            IOptions<AppConfigurationOptions> options,
            IGooglesheetsService googlesheetsService,
            IUserIdentityResolver userIdentityResolver)
        {
            _options = options.Value;
            _googlesheetsService = googlesheetsService;
            _userIdentityResolver = userIdentityResolver;
        }

        public async Task Create(Expense value)
        {
            SheetsService service = await _googlesheetsService.Get();
            value.CreatorEmail = _userIdentityResolver.GetEmail();
            // Insert Expense data
            ValueRange valueRange = new ValueRange();
            valueRange.Values = new List<IList<object>> { new List<object>
            {
                value.Date.ToString("yyyy-MM-dd"),
                "", // Year
                "", // Month
                value.Amount,
                value.Description,
                value.Category,
                value.CreatorEmail
            } };

            SpreadsheetsResource.ValuesResource.AppendRequest appendRequest =
                service.Spreadsheets.Values.Append(
                    valueRange,
                    _options.Googlesheets.SpreadsheetId,
                    _options.Googlesheets.ExpensesRange);

            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            var appendResponse = await appendRequest.ExecuteAsync();

            // Update Expense Year and Month
            // "ExpensesAux!B53:G53" = "53" - > Return the appended row index
            Match match = Regex.Match(appendResponse.Updates.UpdatedRange, @"\d+$");

            if (match.Success)
            {
                valueRange.Values = new List<IList<object>> { new List<object>
                {
                    $"=YEAR(B{match.Value})", // Year
                    $"=MONTH(B{match.Value})", // Month
                } };

                var updateRange = $"{_options.Googlesheets.ExpensesDateRange}{match.Value}";
                SpreadsheetsResource.ValuesResource.UpdateRequest updateRequest = 
                    service.Spreadsheets.Values.Update(valueRange, 
                    _options.Googlesheets.SpreadsheetId,
                    updateRange);
                updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                UpdateValuesResponse updateResponse = updateRequest.Execute();
            }
        }
    }
}
