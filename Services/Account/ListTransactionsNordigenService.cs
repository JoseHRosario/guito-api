using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Output;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace GuitoApi.Services.Account
{
    public class ListTransactionsNordigenService : IListTransactionsService
    {
        private readonly AppConfigurationOptions _options;
        private readonly ILogger<ListTransactionsNordigenService> _logger;
        private readonly IGooglesheetsService _googlesheetsService;
        private HttpClient? _client;

        public ListTransactionsNordigenService(
            IOptions<AppConfigurationOptions> options,
            ILogger<ListTransactionsNordigenService> logger,
            IGooglesheetsService googlesheetsService)
        {
            _options = options.Value;
            _logger = logger;
            _googlesheetsService = googlesheetsService;
        }

        public async Task<TransactionList> List(DateTime? dateFrom, DateTime? dateTo)
        {
            var output = new TransactionList();
            var token = await GetToken();
            if (token == null)
                return output;

            var accountId = await GetAccountId(token);
            if (accountId == null)
                return output;

            output = await GetTransactions(token, accountId, dateFrom, dateTo);
            return output;
        }

        private async Task<TransactionList> GetTransactions(string token, string accountId, DateTime? dateFrom, DateTime? dateTo)
        {
            var output = new TransactionList();
            var client = GetHttpClient();
            var path = $"accounts/{accountId}/transactions/?date_from={GetDateFrom(dateFrom)}&date_to={GetDateTo(dateTo)}";
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Authorization", $"Bearer {token}");
            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var document = JsonDocument.Parse(content);
                var transactions = document.RootElement
                    .GetProperty("transactions")
                    .GetProperty("booked");
                foreach (var transaction in transactions.EnumerateArray())
                {
                    var transactionAmount = transaction
                        .GetProperty("transactionAmount")
                        .GetProperty("amount").GetString();
                    if (transactionAmount == null)
                        continue;

                    var amount = decimal.Parse(transactionAmount);
                    // We only want debits
                    if (amount > 0)
                        continue;

#pragma warning disable CS8604 // Possible null reference argument.
                    var transactionDetail = new TransactionListDetail
                    {
                        Amount = amount * -1,
                        Date = transaction.GetProperty("bookingDate").GetString() == null
                            ? null
                            : DateTime.Parse(transaction.GetProperty("bookingDate").GetString()),
                        Description = transaction.GetProperty("remittanceInformationUnstructured").GetString(),
                        Id = transaction.GetProperty("internalTransactionId").GetString()
                    };
#pragma warning restore CS8604 // Possible null reference argument.
                    output.Transactions.Add(transactionDetail);
                }
            }
            else
            {
                _logger.LogError("Nordigen API Error. Status Code: {0}, Reason Phrase - {1} ", response.StatusCode, response.ReasonPhrase);
                throw new Exception("Failed to get transactions from Nordigen");
            }
            return output;
        }

        private string GetDateFrom(DateTime? date)
        {
            date = date ?? DateTime.Now.AddDays(-20);
            return date.Value.ToString("yyyy-MM-dd");
        }

        private string GetDateTo(DateTime? date)
        {
            date = date ?? DateTime.Now;
            return date.Value.ToString("yyyy-MM-dd");
        }

        private async Task<string?> GetAccountId(string token)
        {
            string? accountId = null;
            var client = GetHttpClient();
            var requisitionId = await GetRequisitionId();
            var path = $"requisitions/{requisitionId}/";
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Authorization", $"Bearer {token}");
            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var document = JsonDocument.Parse(content);
                var accounts = document.RootElement.GetProperty("accounts");
                foreach (var account in accounts.EnumerateArray())
                {
                    var accountGuid = account.GetString();
                    if (accountGuid == null)
                        continue;
                    var iban = await GetAccountIban(token, accountGuid);
                    if (iban == _options.Nordigen.Iban)
                    {
                        accountId = accountGuid;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(accountId) || !response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get account id from Nordigen");
                throw new Exception("Failed to get account id from Nordigen");
            }
            return accountId;
        }

        private async Task<string?> GetRequisitionId()
        {
            string? requisitionId = null;
            SheetsService service = await _googlesheetsService.Get();

            // Read values from the specified range
            SpreadsheetsResource.ValuesResource.GetRequest request =
                service.Spreadsheets.Values.Get(_options.Googlesheets.SpreadsheetId, _options.Googlesheets.RequisitionRange);

            ValueRange response = await request.ExecuteAsync();
            IList<IList<object>> values = response.Values;
            if (values != null && values.Count > 0)
            {
                var value = values.FirstOrDefault();
                requisitionId = value?.FirstOrDefault()?.ToString();
            }
            return requisitionId;
        }

        private async Task<string?> GetAccountIban(string token, string accountId)
        {
            string? iban = null;
            var client = GetHttpClient();
            var path = $"accounts/{accountId}/";
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Authorization", $"Bearer {token}");
            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var document = JsonDocument.Parse(content);
                var property = document.RootElement.GetProperty("iban");
                iban = property.GetString();
            }

            if (string.IsNullOrWhiteSpace(iban) || !response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get account iban from Nordigen");
                throw new Exception("Failed to get account iban from Nordigen");
            }
            return iban;
        }


        private async Task<string?> GetToken()
        {
            string? token = null;
            var client = GetHttpClient();
            var request = new HttpRequestMessage(HttpMethod.Post, "token/new/");
            var payload = new
            {
                secret_id = _options.Nordigen.SecretId,
                secret_key = _options.Nordigen.SecretKey
            };
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");//CONTENT-TYPE header
            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var document = JsonDocument.Parse(content);
                var property = document.RootElement.GetProperty("access");
                token = property.GetString();
            }

            if (string.IsNullOrWhiteSpace(token) || !response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get token from Nordigen");
                throw new Exception("Failed to get token from Nordigen");
            }
            return token;
        }

        private HttpClient GetHttpClient()
        {
            if (_client != null)
                return _client;

            _client = new HttpClient();
            _client.DefaultRequestHeaders.Add("Accept", "application/json");
            _client.BaseAddress = new Uri(_options.Nordigen.Endpoint);
            return _client;
        }
    }
}
