using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using Microsoft.Extensions.Options;

namespace GuitoApi.Services.Account
{
    public class ListTransactionsNordigenService : IListTransactionsService
    {
        private const int MaxLoggedErrorBodyLength = 500;

        private readonly AppConfigurationOptions _options;
        private readonly ILogger<ListTransactionsNordigenService> _logger;
        private readonly IGooglesheetsService _googlesheetsService;
        private readonly HttpClient _client;

        public ListTransactionsNordigenService(
            IOptions<AppConfigurationOptions> options,
            ILogger<ListTransactionsNordigenService> logger,
            IGooglesheetsService googlesheetsService,
            IHttpClientFactory httpClientFactory)
        {
            _options = options.Value;
            _logger = logger;
            _googlesheetsService = googlesheetsService;
            _client = httpClientFactory.CreateClient(nameof(ListTransactionsNordigenService));
            _client.DefaultRequestHeaders.Add("Accept", "application/json");
            _client.BaseAddress = new Uri(_options.Nordigen.Endpoint);
        }

        public async Task<TransactionList> ListAsync(DateTime? dateFrom, DateTime? dateTo,
            CancellationToken cancellationToken = default)
        {
            var token = await GetTokenAsync(cancellationToken);
            var accountId = await GetAccountIdAsync(token, cancellationToken);

            return await GetTransactionsAsync(token, accountId, dateFrom, dateTo, cancellationToken);
        }

        private async Task<TransactionList> GetTransactionsAsync(
            string token, string accountId, DateTime? dateFrom, DateTime? dateTo,
            CancellationToken cancellationToken)
        {
            var output = new TransactionList();
            var response = await SendAuthenticatedRequestAsync(token,
                $"accounts/{accountId}/transactions/?date_from={GetDateFrom(dateFrom)}&date_to={GetDateTo(dateTo)}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateUpstreamErrorExceptionAsync(response, "getting transactions", cancellationToken);
            }

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var transactions = document.RootElement
                .GetProperty("transactions")
                .GetProperty("booked");

            foreach (var transaction in transactions.EnumerateArray())
            {
                var expense = ToTransactionDetailOrNull(transaction);
                if (expense is not null)
                    output.Transactions.Add(expense);
            }

            return output;
        }

        private static TransactionListDetail? ToTransactionDetailOrNull(JsonElement transaction)
        {
            var amountText = transaction
                .GetProperty("transactionAmount")
                .GetProperty("amount")
                .GetString();
            if (amountText is null || !decimal.TryParse(amountText, out var amount))
                return null;

            // We only want debits
            if (amount > 0)
                return null;

            return new TransactionListDetail
            {
                Amount = amount * -1,
                Date = ParseBookingDate(transaction),
                Description = transaction.GetProperty("remittanceInformationUnstructured").GetString(),
                Id = transaction.GetProperty("internalTransactionId").GetString()
            };
        }

        private static DateTime? ParseBookingDate(JsonElement transaction)
        {
            var bookingDate = transaction.GetProperty("bookingDate").GetString();
            return DateTime.TryParse(bookingDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result)
                ? result
                : null;
        }

        private string GetDateFrom(DateTime? date) => FormatDate(date ?? DateTime.Now.AddDays(-20));

        private string GetDateTo(DateTime? date) => FormatDate(date ?? DateTime.Now);

        private static string FormatDate(DateTime date) => date.ToString("yyyy-MM-dd");

        private async Task<string> GetAccountIdAsync(string token, CancellationToken cancellationToken)
        {
            var requisitionId = await GetRequisitionIdAsync(cancellationToken);
            var response = await SendAuthenticatedRequestAsync(token, $"requisitions/{requisitionId}/", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateUpstreamErrorExceptionAsync(response, "getting account id", cancellationToken);
            }

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var accounts = document.RootElement.GetProperty("accounts");
            foreach (var account in accounts.EnumerateArray())
            {
                var accountGuid = account.GetString();
                if (accountGuid is null)
                    continue;

                var iban = await GetAccountIbanAsync(token, accountGuid, cancellationToken);
                if (iban == _options.Nordigen.Iban)
                    return accountGuid;
            }

            _logger.LogError("Failed to get account id from Nordigen");
            throw new ProblemException((int)HttpStatusCode.BadGateway, "Failed to get account id from Nordigen");
        }

        private async Task<string?> GetRequisitionIdAsync(CancellationToken cancellationToken)
        {
            SheetsService service = await _googlesheetsService.GetAsync();
            SpreadsheetsResource.ValuesResource.GetRequest request =
                service.Spreadsheets.Values.Get(_options.Googlesheets.SpreadsheetId, _options.Googlesheets.RequisitionRange);

            ValueRange response = await request.ExecuteAsync(cancellationToken);
            var values = response.Values;
            if (values is not { Count: > 0 })
                return null;

            var firstRow = values.FirstOrDefault();
            return firstRow?.FirstOrDefault()?.ToString();
        }

        private async Task<string> GetAccountIbanAsync(string token, string accountId,
            CancellationToken cancellationToken)
        {
            var response = await SendAuthenticatedRequestAsync(token, $"accounts/{accountId}/", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateUpstreamErrorExceptionAsync(response, "getting account iban", cancellationToken);
            }

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var iban = document.RootElement.GetProperty("iban").GetString();
            if (string.IsNullOrWhiteSpace(iban))
            {
                _logger.LogError("Failed to get account iban from Nordigen");
                throw new ProblemException((int)HttpStatusCode.BadGateway, "Failed to get account iban from Nordigen");
            }

            return iban;
        }

        private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "token/new/");
            var payload = new
            {
                secret_id = _options.Nordigen.SecretId,
                secret_key = _options.Nordigen.SecretKey
            };
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateUpstreamErrorExceptionAsync(response, "getting token", cancellationToken);
            }

            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);
            var token = document.RootElement.GetProperty("access").GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogError("Failed to get token from Nordigen");
                throw new ProblemException((int)HttpStatusCode.BadGateway, "Failed to get token from Nordigen");
            }

            return token;
        }

        private async Task<HttpResponseMessage> SendAuthenticatedRequestAsync(
            string token, string path, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _client.SendAsync(request, cancellationToken);
        }

        // Upstream errors surface the provider's own status and reason, not a generic 500.
        private async Task<ProblemException> CreateUpstreamErrorExceptionAsync(
            HttpResponseMessage response, string operation, CancellationToken cancellationToken)
        {
            var reason = response.ReasonPhrase ?? string.Empty;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // Bodies can be large or provider-internal: cap what reaches the log.
            var bodyExcerpt = body.Length <= MaxLoggedErrorBodyLength
                ? body
                : body[..MaxLoggedErrorBodyLength] + "…";
            _logger.LogError("Nordigen API error while {Operation}. Status Code: {StatusCode}, Reason Phrase: {ReasonPhrase}, Body: {Body}",
                operation, (int)response.StatusCode, reason, bodyExcerpt);

            return new ProblemException((int)response.StatusCode, $"Nordigen API Error: {reason}");
        }
    }
}
