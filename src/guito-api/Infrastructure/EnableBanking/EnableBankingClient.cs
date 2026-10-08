using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>
    /// Typed operations over the Enable Banking HTTP API (issue #89, ADR-0004): start auth
    /// (POST /auth), finish auth (POST /sessions), session status, and transaction pages
    /// (GET /accounts/{uid}/transactions with continuation_key pagination). Errors surface
    /// as EnableBankingApiException carrying the upstream status.
    /// </summary>
    public class EnableBankingClient : IEnableBankingClient
    {
        private readonly IEnableBankingTransport _transport;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public EnableBankingClient(IEnableBankingTransport transport)
        {
            _transport = transport;
        }

        public async Task<EnableBankingStartAuthorization> StartAuthorizationAsync(
            string aspspName, string aspspCountry, string state, string redirectUrl,
            CancellationToken cancellationToken = default)
        {
            var body = JsonSerializer.Serialize(new
            {
                aspsp = new { name = aspspName, country = aspspCountry },
                state,
                redirect_url = redirectUrl,
            }, JsonOptions);
            var response = await _transport.SendAsync(new(HttpMethod.Post, "auth", body), cancellationToken);
            EnsureSuccess(response);
            var root = Parse(response.Body);
            return new EnableBankingStartAuthorization(root.GetProperty("url").GetString() ?? string.Empty);
        }

        public async Task<EnableBankingSession> AuthorizeSessionAsync(string code, CancellationToken cancellationToken = default)
        {
            var body = JsonSerializer.Serialize(new { code }, JsonOptions);
            var response = await _transport.SendAsync(new(HttpMethod.Post, "sessions", body), cancellationToken);
            EnsureSuccess(response);
            return ParseSession(Parse(response.Body));
        }

        public async Task<EnableBankingTransactionsPage> ListTransactionsAsync(
            string accountUid, DateOnly? dateFrom, DateOnly? dateTo, string? continuationKey,
            CancellationToken cancellationToken = default)
        {
            var query = new List<string>();
            if (dateFrom is not null)
                query.Add($"date_from={Uri.EscapeDataString(dateFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
            if (dateTo is not null)
                query.Add($"date_to={Uri.EscapeDataString(dateTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
            if (continuationKey is not null)
                query.Add($"continuation_key={Uri.EscapeDataString(continuationKey)}");
            // ADR-0004/#89: explicit default strategy (range fetch, no history deep-dive).
            query.Add("strategy=default");

            var path = $"accounts/{Uri.EscapeDataString(accountUid)}/transactions{(query.Count > 0 ? $"?{string.Join('&', query)}" : string.Empty)}";
            var response = await _transport.SendAsync(new(HttpMethod.Get, path, null), cancellationToken);
            EnsureSuccess(response);
            return ParseTransactionsPage(Parse(response.Body));
        }

        private static JsonElement Parse(string body)
        {
            try
            {
                return JsonDocument.Parse(body).RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new EnableBankingApiException(502, $"Enable Banking returned malformed JSON: {exception.Message}");
            }
        }

        private static void EnsureSuccess(EnableBankingApiResponse response)
        {
            if (response.StatusCode is < 200 or >= 300)
                throw EnableBankingApiException.FromResponse(response);
        }

        private static EnableBankingSession ParseSession(JsonElement root) => new(
            SessionId: root.GetProperty("session_id").GetString() ?? string.Empty,
            AspspName: root.GetProperty("aspsp").GetProperty("name").GetString() ?? string.Empty,
            AspspCountry: root.GetProperty("aspsp").GetProperty("country").GetString() ?? string.Empty,
            AccessValidUntil: ParseValidUntil(root),
            Accounts: ParseAccounts(root));

        private static DateTime? ParseValidUntil(JsonElement root) =>
            root.TryGetProperty("access", out var access)
            && access.TryGetProperty("valid_until", out var validUntil)
            && validUntil.ValueKind == JsonValueKind.String
            && DateTime.TryParse(validUntil.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;

        private static IReadOnlyList<EnableBankingSessionAccount> ParseAccounts(JsonElement root)
        {
            var accounts = new List<EnableBankingSessionAccount>();
            foreach (var account in root.GetProperty("accounts").EnumerateArray())
            {
                var iban = account.TryGetProperty("account_id", out var accountId)
                    && accountId.TryGetProperty("iban", out var ibanElement)
                    ? ibanElement.GetString()
                    : null;
                var currency = account.TryGetProperty("currency", out var currencyElement)
                    ? currencyElement.GetString()
                    : null;
                accounts.Add(new EnableBankingSessionAccount(
                    Uid: account.GetProperty("uid").GetString() ?? string.Empty,
                    Iban: iban,
                    Name: account.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                    Currency: currency ?? string.Empty));
            }

            return accounts;
        }

        private static EnableBankingTransactionsPage ParseTransactionsPage(JsonElement root)
        {
            var transactions = new List<EnableBankingTransaction>();
            if (root.TryGetProperty("transactions", out var transactionArray))
            {
                foreach (var element in transactionArray.EnumerateArray())
                {
                    var amount = element.GetProperty("transaction_amount").GetProperty("amount").GetString();
                    if (amount is null || !decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedAmount))
                        continue;
                    var bookingDate = element.GetProperty("booking_date").GetString();
                    if (bookingDate is null || !DateOnly.TryParseExact(bookingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                        continue;

                    transactions.Add(new EnableBankingTransaction(
                        TransactionId: GetStringOrNull(element, "transaction_id"),
                        EntryReference: GetStringOrNull(element, "entry_reference"),
                        BookingDate: parsedDate,
                        Amount: parsedAmount,
                        Currency: element.GetProperty("transaction_amount").GetProperty("currency").GetString() ?? string.Empty,
                        CreditDebitIndicator: GetStringOrNull(element, "credit_debit_indicator") ?? string.Empty,
                        Status: GetStringOrNull(element, "status") ?? string.Empty,
                        RemittanceInformation: JoinRemittanceInformation(element),
                        Note: GetStringOrNull(element, "note"),
                        CounterpartyName: PartyName(element, "creditor") ?? PartyName(element, "debtor")));
                }
            }

            var continuationKey = root.TryGetProperty("continuation_key", out var continuation)
                && continuation.ValueKind == JsonValueKind.String
                ? continuation.GetString()
                : null;
            return new EnableBankingTransactionsPage(transactions, continuationKey);
        }

        private static string? GetStringOrNull(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        /// <summary>The counterparty's name (creditor for DBIT rows — the sync_key composite, ADR-0013).</summary>
        private static string? PartyName(JsonElement element, string partyProperty) =>
            element.TryGetProperty(partyProperty, out var party)
            && party.ValueKind == JsonValueKind.Object
            ? GetStringOrNull(party, "name")
            : null;

        private static string? JoinRemittanceInformation(JsonElement element) =>
            element.TryGetProperty("remittance_information", out var remittance)
            && remittance.ValueKind == JsonValueKind.Array
                ? string.Join(' ', remittance.EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item)))
                : null;
    }
}