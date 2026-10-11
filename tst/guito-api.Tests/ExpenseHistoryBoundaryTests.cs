using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using GuitoApi.Infrastructure.Postgres;

namespace GuitoApi.Tests;

public class ExpenseHistoryBoundaryTests
{
    private static IReadOnlyList<PostgresValue> Stored(int id, string date,
        string amount = "-123456789.1234567890123456789", string description = "Original", string category = "Restaurants") =>
        [PostgresValue.FromString(id.ToString()),
         PostgresValue.FromString(date),
         PostgresValue.FromString(amount),
         PostgresValue.FromString(description),
         PostgresValue.FromString(category),
         PostgresValue.FromString("original@example.test"),
         PostgresValue.FromString("2026-01-02T12:00:00.123456Z"),
         PostgresValue.Null()];

    private static int[] Ids(JsonElement page) => page.GetProperty("expenses").EnumerateArray()
        .Select(e => e.GetProperty("id").GetInt32()).ToArray();

    private static string Next(JsonElement page, int pageSize) =>
        $"/Expense/history?pageSize={pageSize}&cursor={page.GetProperty("nextCursor").GetString()}";

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    public async Task History_ShouldRejectPageSizeWithoutStorageActivity_WhenOutOfBounds(string size)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/Expense/history?pageSize={size}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData("Sheets", false, "Development")]
    [InlineData("Postgres", true, "Development")]
    [InlineData("Sheets", true, "Development")]
    [InlineData("Postgres", false, "Production")]
    public async Task History_ShouldRejectWithoutAnyDatastoreActivity_WhenScopeOrAuthorityUnavailable(string store, bool smoke, string environment)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = store, Environment = environment };
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-agent-key");
        if (smoke) client.DefaultRequestHeaders.Add("X-Sheet-Scope", "smoke");

        // Act
        var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task History_ShouldRetrieveEveryRecordInNumericTieOrder_WhenHistoryExceedsLatestCap(int pageSize)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        for (var id = 1; id <= 120; id++) factory.Postgres.SeedExpense(Stored(id, "2026-07-02T00:00:00.123456Z"));
        using var client = factory.CreateClient();

        // Act
        var pages = await ReadAllHistoryPagesAsync(client, pageSize);

        // Assert
        AssertCompleteHistory(pages);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    private sealed record ObservedPage(HttpStatusCode Status, bool NoStore, JsonElement Body);

    private static async Task<List<ObservedPage>> ReadAllHistoryPagesAsync(HttpClient client, int pageSize)
    {
        var pages = new List<ObservedPage>();
        var url = $"/Expense/history?pageSize={pageSize}";
        do
        {
            using var response = await client.GetAsync(url);
            var page = await response.Content.ReadFromJsonAsync<JsonElement>();
            pages.Add(new(response.StatusCode, response.Headers.CacheControl?.NoStore == true, page));
            url = page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? string.Empty : Next(page, pageSize);
        } while (url.Length > 0);
        return pages;
    }

    private static void AssertCompleteHistory(List<ObservedPage> pages)
    {
        var snapshotId = pages[0].Body.GetProperty("snapshotId").GetString();
        Assert.All(pages, page =>
        {
            Assert.Equal(HttpStatusCode.OK, page.Status);
            Assert.True(page.NoStore);
            Assert.Equal(snapshotId, page.Body.GetProperty("snapshotId").GetString());
            Assert.Equal(120, page.Body.GetProperty("totalCount").GetInt64());
        });
        var ids = pages.SelectMany(page => Ids(page.Body)).ToList();
        Assert.Equal(Enumerable.Range(1, 120).Reverse(), ids);
        Assert.Equal(120, ids.Distinct().Count());
    }

    [Fact]
    public async Task History_ShouldFreezeMembershipAndValues_WhenBackdatedAndLowerIdentityTransactionsCommitBetweenPages()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        SeedConcurrentHistory(factory.Postgres);
        using var client = factory.CreateClient();
        var first = await client.GetFromJsonAsync<JsonElement>("/Expense/history?pageSize=1");

        // Act
        using var created = await CommitAndChangeExpensesAsync(client, factory.Postgres);
        var last = await client.GetFromJsonAsync<JsonElement>(Next(first, 1));
        var replay = await client.GetFromJsonAsync<JsonElement>(Next(first, 1));
        var fresh = await client.GetFromJsonAsync<JsonElement>("/Expense/history?pageSize=100");
        var oldSnapshotAgain = await client.GetFromJsonAsync<JsonElement>(Next(first, 1));

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        AssertFrozenHistory(first, last, replay);
        AssertFreshHistory(first, last, fresh, oldSnapshotAgain);
    }

    private static void SeedConcurrentHistory(FakePostgresDataApiClient postgres)
    {
        postgres.SeedExpense(Stored(1, "2026-01-01T00:00:00Z"), committed: false);
        postgres.SeedExpense(Stored(2, "2026-07-02T00:00:00Z"));
        postgres.SeedExpense(Stored(3, "2026-07-01T00:00:00Z"));
    }

    private static async Task<HttpResponseMessage> CommitAndChangeExpensesAsync(HttpClient client, FakePostgresDataApiClient postgres)
    {
        postgres.CommitStagedExpenses();
        var created = await client.PostAsJsonAsync("/Expense", new { bookingDate = "2025-01-01", amount = "0", category = "Test" });
        postgres.ReplaceStoredExpense(3, Stored(3, "2026-07-01T00:00:00Z", "999", "Changed", "Changed category"));
        return created;
    }

    private static void AssertFrozenHistory(JsonElement first, JsonElement last, JsonElement replay)
    {
        Assert.Equal(new[] { 2 }, Ids(first));
        Assert.Equal(new[] { 3 }, Ids(last));
        Assert.Equal(first.GetProperty("snapshotId").GetString(), last.GetProperty("snapshotId").GetString());
        Assert.Equal(2, last.GetProperty("totalCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextCursor").ValueKind);
        Assert.Equal(last.GetRawText(), replay.GetRawText());
        var expense = last.GetProperty("expenses")[0];
        Assert.Equal("Original", expense.GetProperty("description").GetString());
        Assert.Equal("Restaurants", expense.GetProperty("category").GetString());
        Assert.Equal("-123456789.1234567890123456789", expense.GetProperty("amount").GetString());
    }

    private static void AssertFreshHistory(JsonElement first, JsonElement last, JsonElement fresh, JsonElement oldSnapshotAgain)
    {
        Assert.Equal(new[] { 2, 3, 1, 4 }, Ids(fresh));
        Assert.NotEqual(first.GetProperty("snapshotId").GetString(), fresh.GetProperty("snapshotId").GetString());
        Assert.Equal(last.GetRawText(), oldSnapshotAgain.GetRawText());
    }

    [Fact]
    public async Task History_ShouldPreserveStoredPrecisionAndLisbonAuditInstants_WhenSnapshotIsExported()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        var stored = Stored(2147483647, "2026-07-01T23:30:00.123456Z", "0.12345678901234567890123456789").ToArray();
        stored[7] = PostgresValue.FromString("2026-07-02T09:30:00.654321Z");
        factory.Postgres.SeedExpense(stored);
        using var client = factory.CreateClient();

        // Act
        var page = await client.GetFromJsonAsync<JsonElement>("/Expense/history");

        // Assert
        var expense = Assert.Single(page.GetProperty("expenses").EnumerateArray());
        Assert.Equal(2147483647, expense.GetProperty("id").GetInt32());
        Assert.Equal("0.12345678901234567890123456789", expense.GetProperty("amount").GetString());
        Assert.Equal("EUR", expense.GetProperty("currency").GetString());
        Assert.Equal("2026-07-02T00:30:00.123456+01:00", expense.GetProperty("bookingDate").GetString());
        Assert.Equal("2026-01-02T12:00:00.123456+00:00", expense.GetProperty("creationDate").GetString());
        Assert.Equal("2026-07-02T10:30:00.654321+01:00", expense.GetProperty("updateDate").GetString());
        Assert.Equal("original@example.test", expense.GetProperty("creatorEmail").GetString());
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{00000000-0000-0000-0000-000000000000}")]
    public async Task History_ShouldRejectInvalidOrUnknownCursor_WhenSupplied(string cursor)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/Expense/history?cursor={Uri.EscapeDataString(cursor)}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(factory.Postgres.ExecutedSql, s => s.Contains("INSERT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task History_ShouldRejectExpiredCursorEvenAfterAnotherSnapshot_WhenDatabaseClockReachesExpiry()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        factory.Postgres.SeedExpense(Stored(1, "2026-07-01T00:00:00Z"));
        factory.Postgres.SeedExpense(Stored(2, "2026-07-02T00:00:00Z"));
        using var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>("/Expense/history?pageSize=1");

        // Act
        var wrongSize = await client.GetAsync(Next(page, 2));
        var tampered = await client.GetAsync($"/Expense/history?pageSize=1&cursor={Guid.NewGuid()}");
        factory.Postgres.DatabaseNow = page.GetProperty("expiresAt").GetDateTimeOffset();
        await client.GetFromJsonAsync<JsonElement>("/Expense/history?pageSize=1");
        var expired = await client.GetAsync(Next(page, 1));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, wrongSize.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.DoesNotContain("expenses", await expired.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_ShouldRejectExpiryDuringContinuation_WhenPageReadFollowsCursorLookup(bool cleanup)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        factory.Postgres.SeedExpense(Stored(1, "2026-07-01T00:00:00Z"));
        factory.Postgres.SeedExpense(Stored(2, "2026-07-02T00:00:00Z"));
        using var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>("/Expense/history?pageSize=1");
        factory.Postgres.ExpireBeforeHistoryPage = true;
        factory.Postgres.CleanUpBeforeHistoryPage = cleanup;

        // Act
        var response = await client.GetAsync(Next(page, 1));
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.DoesNotContain("expenses", body);
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.Unauthorized)]
    [InlineData("unknown", null, HttpStatusCode.Forbidden)]
    [InlineData(null, "not-a-jwt", HttpStatusCode.Forbidden)]
    [InlineData("test-agent-key", null, HttpStatusCode.OK)]
    public async Task History_ShouldRespectIndependentAuthGates_WhenCredentialsArePresented(string? agentKey, string? humanToken, HttpStatusCode expected)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var secured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfiguration:Authentication:ValidateApiKey"] = "true",
                ["AppConfiguration:Authentication:ValidateIdToken"] = "true"
            })));
        using var client = secured.CreateClient();
        if (agentKey is not null) client.DefaultRequestHeaders.Add("X-Api-Key", agentKey);
        if (humanToken is not null) client.DefaultRequestHeaders.Add("x-google-idtoken", humanToken);

        // Act
        var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK) Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    [Fact]
    public async Task History_ShouldRequireAuthorizationOnContinuation_WhenCursorWasIssuedToAuthorizedCaller()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        factory.Postgres.SeedExpense(Stored(1, "2026-07-01T00:00:00Z"));
        factory.Postgres.SeedExpense(Stored(2, "2026-07-02T00:00:00Z"));
        using var secured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["AppConfiguration:Authentication:ValidateApiKey"] = "true" })));
        using var client = secured.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-agent-key");
        var page = await client.GetFromJsonAsync<JsonElement>("/Expense/history?pageSize=1");
        factory.Postgres.ExecutedSql.Clear();
        client.DefaultRequestHeaders.Remove("X-Api-Key");

        // Act
        var response = await client.GetAsync(Next(page, 1));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task History_ShouldReplaySameNextCursor_WhenIntermediatePageIsRetried()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        for (var id = 1; id <= 3; id++) factory.Postgres.SeedExpense(Stored(id, "2026-07-01T00:00:00Z"));
        using var client = factory.CreateClient();
        var first = await client.GetFromJsonAsync<JsonElement>("/Expense/history?pageSize=1");

        // Act
        var middle = await client.GetFromJsonAsync<JsonElement>(Next(first, 1));
        var replay = await client.GetFromJsonAsync<JsonElement>(Next(first, 1));
        var final = await client.GetFromJsonAsync<JsonElement>(Next(middle, 1));

        // Assert
        Assert.Equal(new[] { 2 }, Ids(middle));
        Assert.Equal(middle.GetRawText(), replay.GetRawText());
        Assert.Equal(new[] { 1 }, Ids(final));
        Assert.Equal(JsonValueKind.Null, final.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_ShouldFailInsteadOfReturningPartialPage_WhenProviderReturnsFewerRowsThanSnapshotCount(bool sentinel)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres" };
        factory.Postgres.NextResults.Enqueue(new PostgresResult([], [[PostgresValue.FromString(Guid.NewGuid().ToString()),
            PostgresValue.FromString("2026-10-12T00:00:00Z"), PostgresValue.FromLong(1), PostgresValue.FromLong(1)]], 0));
        factory.Postgres.NextResults.Enqueue(new PostgresResult([], sentinel
            ? [Enumerable.Repeat(PostgresValue.Null(), 8).Append(PostgresValue.FromString("false")).ToList()] : [], 0));
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("expenses", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task History_ShouldNotReturnPartialSuccess_WhenDatastoreFails()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        factory.Postgres.ExecuteException = new InvalidOperationException("hermetic provider failure");
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("expenses", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("?cursor=")]
    [InlineData("?cursor=%20")]
    public async Task History_ShouldRejectEmptyCursorWithoutStorageActivity_WhenCursorParameterIsPresent(string query)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/Expense/history{query}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task History_ShouldReturnCompleteEmptySnapshot_WhenNoExpensesExist()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(page.GetProperty("expenses").EnumerateArray());
        Assert.Equal(0, page.GetProperty("totalCount").GetInt64());
        Assert.True(Guid.TryParse(page.GetProperty("snapshotId").GetString(), out _));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }
}
