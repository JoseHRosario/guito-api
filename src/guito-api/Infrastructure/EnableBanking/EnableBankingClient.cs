using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>
    /// Enable Banking adapter implementing the application-layer provider ports
    /// (issues #89/#90, ADR-0004/0011): IBankConsentProvider (POST /auth, POST /sessions)
    /// and IBankTransactionProvider (GET /accounts/{uid}/transactions with continuation_key
    /// pagination, strategy=default). EB wire JSON is parsed here and mapped onto Model
    /// types — the application layer never sees an EB type. Failures surface at the port
    /// boundary as ProblemException (classified by the mappers below), never as EB types.
    /// </summary>
    public class EnableBankingClient : IBankConsentProvider, IBankTransactionProvider
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

        public async Task<BankConsentStart> StartAuthorizationAsync(
            string aspspName, string aspspCountry, string state, string redirectUrl,
            CancellationToken cancellationToken = default)
        {
            var body = JsonSerializer.Serialize(new
            {
                aspsp = new { name = aspspName, country = aspspCountry },
                state,
                redirect_url = redirectUrl,
            }, JsonOptions);
            var response = await SendAsync(new(HttpMethod.Post, "auth", body), cancellationToken);
            var root = Parse(response.Body);
            return new BankConsentStart(root.GetProperty("url").GetString() ?? string.Empty);
        }

        public async Task<BankConsentSession> FinishAuthorizationAsync(string code, CancellationToken cancellationToken = default)
        {
            var body = JsonSerializer.Serialize(new { code }, JsonOptions);
            var response = await SendAsync(new(HttpMethod.Post, "sessions", body), cancellationToken);
            return ParseSession(Parse(response.Body));
        }

        public async Task<BankTransactionSourcePage> ListTransactionsAsync(
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
            var response = await SendAsync(new(HttpMethod.Get, path, null), cancellationToken);
            return ParseTransactionsPage(Parse(response.Body));
        }

        /// <summary>
        /// Single error boundary: EB failures are classified here so ports only ever
        /// throw ProblemException — fetch semantics (409 reconnect / 429 rate limit /
        /// 502) vs consent-flow semantics (rejected request/code → 400 / 502) differ.
        /// </summary>
        private async Task<EnableBankingApiResponse> SendAsync(
            EnableBankingApiRequest request, CancellationToken cancellationToken)
        {
            var response = await _transport.SendAsync(request, cancellationToken);
            if (response.StatusCode is < 200 or >= 300)
            {
                var exception = EnableBankingApiException.FromResponse(response);
                throw IsConsentRequest(request.Path)
                    ? EnableBankingConsentErrorMapper.ToProblemException(exception)
                    : EnableBankingFetchErrorMapper.ToProblemException(exception);
            }

            return response;
        }

        private static bool IsConsentRequest(string path) => path is "auth" or "sessions";

        private static JsonElement Parse(string body)
        {
            try
            {
                return JsonDocument.Parse(body).RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new ProblemException(502, $"Enable Banking returned malformed JSON: {exception.Message}");
            }
        }

        private static BankConsentSession ParseSession(JsonElement root) => new(
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

        private static IReadOnlyList<BankConsentAccount> ParseAccounts(JsonElement root)
        {
            var accounts = new List<BankConsentAccount>();
            foreach (var account in root.GetProperty("accounts").EnumerateArray())
            {
                var iban = account.TryGetProperty("account_id", out var accountId)
                    && accountId.TryGetProperty("iban", out var ibanElement)
                    ? ibanElement.GetString()
                    : null;
                var currency = account.TryGetProperty("currency", out var currencyElement)
                    ? currencyElement.GetString()
                    : null;
                accounts.Add(new BankConsentAccount(
                    Uid: account.GetProperty("uid").GetString() ?? string.Empty,
                    Iban: iban,
                    Name: account.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                    Currency: currency ?? string.Empty));
            }

            return accounts;
        }

        private static BankTransactionSourcePage ParseTransactionsPage(JsonElement root)
        {
            var transactions = new List<BankTransactionSource>();
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

                    transactions.Add(new BankTransactionSource(
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
            return new BankTransactionSourcePage(transactions, continuationKey);
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