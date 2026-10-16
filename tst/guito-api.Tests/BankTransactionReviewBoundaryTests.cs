using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.Infrastructure.Postgres;

namespace GuitoApi.Tests;

public class BankTransactionReviewBoundaryTests
{
    private static CustomWebApplicationFactory Factory(BankDatabase database)
    {
        var factory = new CustomWebApplicationFactory
        {
            UseEnableBankingProvider = true, UseRealBankTransactionRepository = true
        };
        factory.Postgres.ExecuteBank = database.Execute;
        return factory;
    }

    [Fact]
    public async Task Get_ShouldReturnAllStoredRows_WithExactAmountsAndSeparateStatuses()
    {
        var database = new BankDatabase();
        database.Seed(2, "ignored");
        database.Seed(10, "matched");
        database.Seed(long.MaxValue, "needsReview", "2026-10-06");
        using var factory = Factory(database);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/BankTransaction");
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { long.MaxValue, 10L, 2L }, rows!.Select(r => r.GetProperty("id").GetInt64()));
        AssertUnlinkedTransactionDetails(rows[0]);
        AssertLinkedExpenseDetails(rows[1].GetProperty("linkedExpense"));
    }

    private static void AssertUnlinkedTransactionDetails(JsonElement row)
    {
        Assert.Equal("9999999999999999.99", row.GetProperty("amount").GetString());
        Assert.Equal("BOOK", row.GetProperty("status").GetString());
        Assert.Equal("needsReview", row.GetProperty("reviewStatus").GetString());
        Assert.Equal("eb-account", row.GetProperty("accountUid").GetString());
        Assert.Equal("2026-10-06", row.GetProperty("bookingDate").GetString());
        Assert.Equal("EUR", row.GetProperty("currency").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("remittanceInformation").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("linkedExpense").ValueKind);
        Assert.Equal(3L, row.GetProperty("suggestedCategory").GetProperty("id").GetInt64());
    }

    private static void AssertLinkedExpenseDetails(JsonElement linked)
    {
        Assert.Equal(2147483647, linked.GetProperty("id").GetInt32());
        Assert.Equal("2026-07-01T13:34:56.123456+01:00", linked.GetProperty("bookingDate").GetString());
        Assert.Equal("12345678901234567890.12345678", linked.GetProperty("amount").GetString());
        Assert.Equal("EUR", linked.GetProperty("currency").GetString());
        Assert.Equal("Lunch", linked.GetProperty("description").GetString());
        Assert.Equal("Restaurants", linked.GetProperty("category").GetString());
    }

    [Theory]
    [InlineData("all", 3)]
    [InlineData("needsReview", 1)]
    [InlineData("matched", 1)]
    [InlineData("ignored", 1)]
    public async Task Get_ShouldFilterStoredReviewStatus_WhenFilterIsValid(string filter, int count)
    {
        var database = new BankDatabase();
        database.Seed(2, "ignored"); database.Seed(10, "matched"); database.Seed(11, "needsReview");
        using var factory = Factory(database);
        using var client = factory.CreateClient();

        var rows = await client.GetFromJsonAsync<JsonElement[]>($"/BankTransaction?reviewStatus={filter}");

        Assert.Equal(count, rows!.Length);
        if (filter != "all") Assert.All(rows, row => Assert.Equal(filter, row.GetProperty("reviewStatus").GetString()));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("NeedsReview")]
    [InlineData("")]
    public async Task Get_ShouldRejectInvalidFilter_WithoutTransportActivity(string filter)
    {
        using var factory = Factory(new BankDatabase());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/BankTransaction?reviewStatus={filter}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task Post_ShouldPersistReversibleIdempotentReviewActions_WhenBodiesAreEmpty()
    {
        var database = new BankDatabase();
        database.Seed(long.MaxValue, "needsReview");
        database.Seed(2, "needsReview");
        using var factory = Factory(database);
        using var client = factory.CreateClient();

        await AssertReviewTransitionAsync(client, "ignore", "ignored");
        await AssertReviewTransitionAsync(client, "ignore", "ignored");
        await AssertReviewTransitionAsync(client, "review", "needsReview");
        await AssertReviewTransitionAsync(client, "review", "needsReview");
    }

    private static async Task AssertReviewTransitionAsync(HttpClient client, string action, string status)
    {
        var response = await client.PostAsync($"/BankTransaction/{long.MaxValue}/{action}", null);
        var row = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(long.MaxValue, row.GetProperty("id").GetInt64());
        Assert.Equal(status, row.GetProperty("reviewStatus").GetString());
        Assert.Equal(status, rows![0].GetProperty("reviewStatus").GetString());
        Assert.Equal("needsReview", rows[1].GetProperty("reviewStatus").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("linkedExpense").ValueKind);
    }

    [Theory]
    [InlineData("ignore")]
    [InlineData("review")]
    public async Task Post_ShouldRejectMissingAndMatchedRows_WithoutChangingLinks(string action)
    {
        var database = new BankDatabase(); database.Seed(10, "matched");
        using var factory = Factory(database);
        using var client = factory.CreateClient();

        var missing = await client.PostAsync($"/BankTransaction/99/{action}", null);
        var matched = await client.PostAsync($"/BankTransaction/10/{action}", null);
        var rows = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, matched.StatusCode);
        var row = Assert.Single(rows!);
        Assert.Equal("matched", row.GetProperty("reviewStatus").GetString());
        Assert.Equal(int.MaxValue, row.GetProperty("linkedExpense").GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("9223372036854775808")]
    public async Task Post_ShouldRejectInvalidIds_WithoutTransportActivity(string id)
    {
        using var factory = Factory(new BankDatabase());
        using var client = factory.CreateClient();

        var ignored = await client.PostAsync($"/BankTransaction/{id}/ignore", null);
        var reviewed = await client.PostAsync($"/BankTransaction/{id}/review", null);

        Assert.Equal(HttpStatusCode.BadRequest, ignored.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reviewed.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData("ignore")]
    [InlineData("review")]
    public async Task Post_ShouldNotOverwriteConcurrentMatch_WhenItWinsBeforeGuardedUpdate(string action)
    {
        var database = new BankDatabase { MatchBeforeUpdate = true }; database.Seed(10, "needsReview");
        using var factory = Factory(database);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/BankTransaction/10/{action}", null);
        var rows = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var row = Assert.Single(rows!);
        Assert.Equal("matched", row.GetProperty("reviewStatus").GetString());
        Assert.Equal(int.MaxValue, row.GetProperty("linkedExpense").GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("GET", "/BankTransaction")]
    [InlineData("POST", "/BankTransaction/10/ignore")]
    [InlineData("POST", "/BankTransaction/10/review")]
    public async Task Request_ShouldReturnProviderFailure_NotEmptyOrPartialSuccess(string method, string path)
    {
        var database = new BankDatabase(); database.Seed(10, "needsReview");
        using var factory = Factory(database);
        factory.Postgres.ExecuteException = new Amazon.RDSDataService.AmazonRDSDataServiceException("response size limit exceeded");
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Get_ShouldRejectUnrepresentableLinkedAmount_WithoutRoundingAnyRow()
    {
        var database = new BankDatabase(); database.Seed(10, "matched");
        database.LinkedAmount = "0.123456789012345678901234567891";
        using var factory = Factory(database);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/BankTransaction");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Get_ShouldReturnCompleteUncappedResult_WhenRowsExceedLatestExpenseCap()
    {
        var database = new BankDatabase();
        for (var id = 1; id <= 151; id++) database.Seed(id, "needsReview");
        using var factory = Factory(database);
        using var client = factory.CreateClient();

        var rows = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        Assert.Equal(151, rows!.Length);
        Assert.Equal(Enumerable.Range(1, 151).Reverse().Select(id => (long)id), rows.Select(row => row.GetProperty("id").GetInt64()));
    }

    [Theory]
    [InlineData("matched", true)]
    [InlineData("unknown", true)]
    [InlineData("ignored", false)]
    public async Task Get_ShouldFailInvalidBackendState_InsteadOfPresentingFalseReviewCompletion(string status, bool missingLink)
    {
        var database = new BankDatabase(); database.Seed(10, status);
        database.OverrideLink = missingLink ? PostgresValue.Null() : PostgresValue.FromLong(int.MaxValue);
        using var factory = Factory(database);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/BankTransaction");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Get_ShouldReturnEmptyArray_WhenNoStoredRowsExist()
    {
        using var factory = Factory(new BankDatabase());
        using var client = factory.CreateClient();

        var rows = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        Assert.Empty(rows!);
    }

    [Fact]
    public async Task Sync_ShouldDefaultNewRowsToNeedsReview_AndPreserveIgnoredRowsOnResync()
    {
        var database = new BankDatabase();
        using var factory = Factory(database);
        ConfigureSyncProvider(factory);
        using var client = factory.CreateClient();

        await AssertInitialSyncAsync(client);
        await AssertIgnoredResyncAsync(client);
    }

    private const string SyncPage = """
        {"transactions":[
          {"entry_reference":"booked", "transaction_amount":{"amount":"-77.93","currency":"EUR"},
           "credit_debit_indicator":"DBIT", "status":"BOOK", "booking_date":"2026-10-05"},
          {"entry_reference":"income", "transaction_amount":{"amount":"300.00","currency":"EUR"},
           "credit_debit_indicator":"CRDT", "status":"BOOK", "booking_date":"2026-10-05"},
          {"entry_reference":"pending", "transaction_amount":{"amount":"-5.00","currency":"EUR"},
           "credit_debit_indicator":"DBIT", "status":"PDNG", "booking_date":"2026-10-05"}
        ], "continuation_key":null}
        """;

    private static void ConfigureSyncProvider(CustomWebApplicationFactory factory)
    {
        factory.BankAccounts.Accounts.Add(new GuitoApi.Model.BankAccountSummary
        {
            Uid = "eb-account", SessionId = "session", Name = "Main", Currency = "EUR",
            AspspCountry = "PT", AspspName = "CGD", ConsentStatus = "VALID"
        });
        factory.EnableBankingTransport.Enqueue(200, SyncPage);
        factory.EnableBankingTransport.Enqueue(200, SyncPage);
    }

    private static async Task AssertInitialSyncAsync(HttpClient client)
    {
        var response = await client.PostAsync("/BankTransaction/sync", null);
        var rows = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(rows!);
        Assert.Equal("needsReview", row.GetProperty("reviewStatus").GetString());
        Assert.Equal("BOOK", row.GetProperty("status").GetString());
        Assert.Equal("77.93", row.GetProperty("amount").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("linkedExpense").ValueKind);
    }

    private static async Task AssertIgnoredResyncAsync(HttpClient client)
    {
        var ignored = await client.PostAsync("/BankTransaction/1/ignore", null);
        var response = await client.PostAsync("/BankTransaction/sync", null);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        Assert.Equal(HttpStatusCode.OK, ignored.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, result.GetProperty("new").GetInt32());
        var persisted = Assert.Single(rows!);
        Assert.Equal("ignored", persisted.GetProperty("reviewStatus").GetString());
    }

    [Theory]
    [InlineData("2026-01-01T12:34:56Z", "2026-01-01T12:34:56+00:00")]
    [InlineData("2026-10-25T00:30:00Z", "2026-10-25T01:30:00+01:00")]
    [InlineData("2026-10-25T01:30:00Z", "2026-10-25T01:30:00+00:00")]
    public async Task Get_ShouldPresentLinkedOccurrenceWithLisbonOffset_AcrossWinterAndDst(string instant, string expected)
    {
        var database = new BankDatabase { LinkedBookingDate = instant }; database.Seed(10, "matched");
        using var factory = Factory(database);
        using var client = factory.CreateClient();

        var rows = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        var row = Assert.Single(rows!);
        Assert.Equal(expected, row.GetProperty("linkedExpense").GetProperty("bookingDate").GetString());
    }

    [Theory]
    [InlineData(BankDatabase.LinkedExpenseIdColumn, "2147483648")]
    [InlineData(BankDatabase.LinkedBookingDateColumn, "not-a-timestamp")]
    [InlineData(BankDatabase.LinkedAmountColumn, null)]
    [InlineData(BankDatabase.AmountColumn, "NaN")]
    public async Task Get_ShouldRejectMalformedProviderRows_InsteadOfReturningPartialDetails(int column, string? value)
    {
        var database = new BankDatabase(); database.Seed(10, "matched");
        database.Overrides[column] = value is null ? PostgresValue.Null() : PostgresValue.FromString(value);
        using var factory = Factory(database);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/BankTransaction");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Theory]
    [InlineData("ignore")]
    [InlineData("review")]
    public async Task Post_ShouldRejectIneligibleUnlinkedStatus_WithoutReplacingIt(string action)
    {
        var database = new BankDatabase(); database.Seed(10, "unknown");
        using var factory = Factory(database);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/BankTransaction/10/{action}", null);
        var persisted = await client.GetAsync("/BankTransaction");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, persisted.StatusCode);
    }

    [Theory]
    [InlineData("ignore")]
    [InlineData("review")]
    public async Task Post_ShouldRejectAnExistingLink_EvenWhenProviderStateIsIncoherent(string action)
    {
        var database = new BankDatabase { OverrideLink = PostgresValue.FromLong(int.MaxValue) };
        database.Seed(10, "needsReview");
        using var factory = Factory(database);
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/BankTransaction/10/{action}", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private sealed class BankDatabase
    {
        public const int IdColumn = 0;
        public const int AccountUidColumn = 1;
        public const int BookingDateColumn = 2;
        public const int AmountColumn = 3;
        public const int CurrencyColumn = 4;
        public const int RemittanceColumn = 5;
        public const int SuggestedCategoryIdColumn = 6;
        public const int SuggestedCategoryNameColumn = 7;
        public const int StatusColumn = 8;
        public const int ReviewStatusColumn = 9;
        public const int LinkedExpenseIdColumn = 10;
        public const int LinkedBookingDateColumn = 11;
        public const int LinkedAmountColumn = 12;

        private readonly List<PostgresValue[]> _rows = [];
        private readonly HashSet<string> _syncKeys = [];
        public bool MatchBeforeUpdate { get; set; }
        public string? LinkedAmount { get; set; }
        public string? LinkedBookingDate { get; set; }
        public PostgresValue? OverrideLink { get; set; }
        public Dictionary<int, PostgresValue> Overrides { get; } = [];
        public void Seed(long id, string reviewStatus, string day = "2026-10-05") => _rows.Add([
            PostgresValue.FromString(id.ToString()), PostgresValue.FromString("eb-account"),
            PostgresValue.FromString(day), PostgresValue.FromString("9999999999999999.99"),
            PostgresValue.FromString("EUR"), PostgresValue.Null(), PostgresValue.FromLong(3),
            PostgresValue.FromString("Restaurants"), PostgresValue.FromString("BOOK"), PostgresValue.FromString(reviewStatus),
            reviewStatus == "matched" ? PostgresValue.FromLong(int.MaxValue) : PostgresValue.Null(),
            PostgresValue.FromString("2026-07-01T12:34:56.123456Z"),
            PostgresValue.FromString("12345678901234567890.12345678"),
            PostgresValue.FromString("Lunch"), PostgresValue.FromString("Restaurants"), PostgresValue.FromLong(0)]);

        public PostgresResult Execute(string sql, IReadOnlyList<PostgresParameter>? parameters)
        {
            ApplyOverrides();
            var values = parameters?.ToDictionary(parameter => parameter.Name, parameter => parameter.Value) ?? [];
            if (sql.StartsWith("INSERT INTO bank_transactions", StringComparison.Ordinal))
                return Insert(values);
            var rows = sql.Contains("UPDATE bank_transactions") ? ChangeReviewStatus(sql, values) : _rows;
            rows = FilterRows(rows, sql, values);
            rows = OrderAndLimitRows(rows, sql);
            return new PostgresResult([], rows.Cast<IReadOnlyList<PostgresValue>>().ToList(), 0);
        }

        private void ApplyOverrides()
        {
            foreach (var row in _rows)
            {
                foreach (var (column, value) in Overrides) row[column] = value;
                if (LinkedAmount is not null) row[LinkedAmountColumn] = PostgresValue.FromString(LinkedAmount);
                if (OverrideLink is not null) row[LinkedExpenseIdColumn] = OverrideLink;
                if (LinkedBookingDate is not null) row[LinkedBookingDateColumn] = PostgresValue.FromString(LinkedBookingDate);
            }
        }

        private PostgresResult Insert(IReadOnlyDictionary<string, PostgresValue> values)
        {
            if (!_syncKeys.Add(values["sync_key"].StringValue!)) return new PostgresResult([], [], 0);
            Seed(_rows.Count + 1, values.TryGetValue("review_status", out var review) ? review.StringValue! : "needsReview");
            PopulateInsertedRow(_rows[^1], values);
            return new PostgresResult([], [[PostgresValue.FromLong(_rows.Count)]], 1);
        }

        private static void PopulateInsertedRow(PostgresValue[] row, IReadOnlyDictionary<string, PostgresValue> values)
        {
            row[AccountUidColumn] = values["account_uid"];
            row[BookingDateColumn] = values["booking_date"];
            row[AmountColumn] = PostgresValue.FromString(values["amount"].DoubleValue!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            row[CurrencyColumn] = values["currency"];
            row[RemittanceColumn] = values["remittance_information"];
            row[SuggestedCategoryIdColumn] = PostgresValue.Null();
            row[SuggestedCategoryNameColumn] = PostgresValue.Null();
            row[StatusColumn] = values["status"];
        }

        private IEnumerable<PostgresValue[]> ChangeReviewStatus(string sql, IReadOnlyDictionary<string, PostgresValue> values)
        {
            var targets = _rows.Where(row => !sql.Contains("WHERE id = :id")
                || long.Parse(row[IdColumn].StringValue!) == values["id"].LongValue).ToList();
            if (MatchBeforeUpdate) MatchRows(targets);
            var eligible = targets.Where(row => IsEligibleForReview(row, sql)).ToList();
            foreach (var row in eligible) row[ReviewStatusColumn] = values["review_status"];
            return eligible;
        }

        private static void MatchRows(IEnumerable<PostgresValue[]> rows)
        {
            foreach (var row in rows)
            {
                row[ReviewStatusColumn] = PostgresValue.FromString("matched");
                row[LinkedExpenseIdColumn] = PostgresValue.FromLong(int.MaxValue);
            }
        }

        private static bool IsEligibleForReview(PostgresValue[] row, string sql) =>
            (!sql.Contains("expense_id IS NULL") || row[LinkedExpenseIdColumn].IsNull)
            && (!sql.Contains("review_status IN ('needsReview', 'ignored')")
                || row[ReviewStatusColumn].StringValue is "needsReview" or "ignored");

        private static IEnumerable<PostgresValue[]> FilterRows(
            IEnumerable<PostgresValue[]> rows, string sql, IReadOnlyDictionary<string, PostgresValue> values)
        {
            if (sql.Contains("bt.id = :id"))
                rows = rows.Where(row => long.Parse(row[IdColumn].StringValue!) == values["id"].LongValue);
            if (sql.Contains("bt.review_status = :review_status"))
                rows = rows.Where(row => row[ReviewStatusColumn].StringValue == values["review_status"].StringValue);
            return rows;
        }

        private static IEnumerable<PostgresValue[]> OrderAndLimitRows(IEnumerable<PostgresValue[]> rows, string sql)
        {
            if (sql.Contains("ORDER BY bt.booking_date DESC"))
                rows = rows.OrderByDescending(row => row[BookingDateColumn].StringValue)
                    .ThenByDescending(row => sql.Contains("bt.id DESC") ? long.Parse(row[IdColumn].StringValue!) : 0);
            return sql.Contains("LIMIT", StringComparison.Ordinal) ? rows.Take(100) : rows;
        }
    }
}
