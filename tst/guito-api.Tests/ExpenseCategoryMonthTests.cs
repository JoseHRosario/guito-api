using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GuitoApi.Tests;

public class ExpenseCategoryMonthTests
{
    [Fact]
    public async Task List_ShouldFailRatherThanRound_WhenDatabaseAmountCannotBeRepresentedExactly()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        factory.Postgres.SeedExpense(1, "2026-10-10T10:00:00Z", "0.12345678901234567890123456789", "Test");
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/Expense?category=Test&month=2026-10");

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("exact", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, null, null, HttpStatusCode.Unauthorized)]
    [InlineData("unknown", "unknown", null, HttpStatusCode.Forbidden)]
    [InlineData("test-agent-key", "test-agent-key", null, HttpStatusCode.OK)]
    [InlineData(null, "test-agent-key", null, HttpStatusCode.Unauthorized)]
    [InlineData(null, "Bearer malformed.jwt", null, HttpStatusCode.Unauthorized)]
    [InlineData(null, "Bearer malformed.jwt", "malformed.jwt", HttpStatusCode.Forbidden)]
    [InlineData("test-agent-key", "Bearer malformed.jwt", "malformed.jwt", HttpStatusCode.Forbidden)]
    public async Task List_ShouldPreserveIndependentAuthGates_WhenAuthenticationEnabled(string? key, string? authorization, string? googleToken, HttpStatusCode expected)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var host = AuthenticatedHost(factory);
        using var client = host.CreateClient();
        AddAuthHeaders(client, key, authorization, googleToken);

        // Act
        var response = await client.GetAsync("/Expense?category=Test&month=2026-10");

        // Assert
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK) Assert.Empty(factory.Postgres.ExecutedSql);
        else Assert.Equal(2, factory.Postgres.ExecutedSql.Count);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> AuthenticatedHost(CustomWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfiguration:Authentication:ValidateApiKey"] = "true",
                ["AppConfiguration:Authentication:ValidateIdToken"] = "true"
            })));

    private static void AddAuthHeaders(HttpClient client, string? key, string? authorization, string? googleToken)
    {
        if (key is not null) client.DefaultRequestHeaders.Add("X-Api-Key", key);
        if (authorization is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        if (googleToken is not null) client.DefaultRequestHeaders.Add("x-google-idtoken", googleToken);
    }

    [Theory]
    [InlineData("test")]
    [InlineData("%20Test%20")]
    [InlineData("Unseeded")]
    [InlineData("Test%27%20OR%201%3D1--")]
    public async Task List_ShouldRejectUnknownExactCategory_WhenMirrorDoesNotContainName(string category)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/Expense?category={category}&month=2026-10");

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("SELECT id FROM categories WHERE name = :name", Assert.Single(factory.Postgres.ExecutedSql));
        Assert.Equal(Uri.UnescapeDataString(category), Assert.Single(factory.Postgres.ExecutedParameters)!.Single().Value.StringValue);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    [Theory]
    [InlineData("Sheets", false)]
    [InlineData("Sheets", true)]
    [InlineData("Postgres", true)]
    public async Task List_ShouldReturnConflictWithoutAnyDatastoreRead_WhenSelectedStoreIsSheets(string store, bool smoke)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = store };
        using var client = factory.CreateClient();
        if (smoke) client.DefaultRequestHeaders.Add("X-Sheet-Scope", "smoke");

        // Act
        var response = await client.GetAsync("/Expense?category=Test&month=2026-10");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    [Fact]
    public async Task List_ShouldUsePostgres_WhenProductionSelectsPostgres()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Environment = "Production", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-agent-key");

        // Act
        var response = await client.GetAsync("/Expense?category=Test&month=2026-10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    [Fact]
    public async Task List_ShouldReturnAllMatches_WhenMoreThanLatestCapExist()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        for (var id = 1; id <= 137; id++) factory.Postgres.SeedExpense(id, "2026-10-10T10:00:00Z", "1", "Test");
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/Expense?category=Test&month=2026-10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expenses = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("expenses");
        Assert.Equal(137, expenses.GetArrayLength());
        Assert.Equal(Enumerable.Range(1, 137).Reverse(), expenses.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
        Assert.DoesNotContain("LIMIT", factory.Postgres.ExecutedSql.Last(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0001-01", "0001-01-01T00:36:00.0000000+00:00", "0001-02-01T00:36:00.0000000+00:00")]
    [InlineData("1916-10", "1916-09-30T23:00:00.0000000+00:00", "1916-10-31T23:00:00.0000000+00:00")]
    [InlineData("1916-11", "1916-10-31T23:00:00.0000000+00:00", "1916-12-01T00:00:00.0000000+00:00")]
    [InlineData("1917-02", "1917-02-01T00:00:00.0000000+00:00", "1917-03-01T00:00:00.0000000+00:00")]
    [InlineData("1917-03", "1917-03-01T00:00:00.0000000+00:00", "1917-03-31T23:00:00.0000000+00:00")]
    [InlineData("2026-03", "2026-03-01T00:00:00.0000000+00:00", "2026-03-31T23:00:00.0000000+00:00")]
    [InlineData("2026-12", "2026-12-01T00:00:00.0000000+00:00", "2027-01-01T00:00:00.0000000+00:00")]
    [InlineData("2024-02", "2024-02-01T00:00:00.0000000+00:00", "2024-03-01T00:00:00.0000000+00:00")]
    [InlineData("9999-12", "9999-12-01T00:00:00.0000000+00:00", "10000-01-01T00:00:00+00:00")]
    public async Task List_ShouldBindCalendarMonthBoundaries_WhenDstLeapYearOrYearRollover(string month, string start, string end)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/Expense?category=Test&month={month}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parameters = factory.Postgres.ExecutedParameters.Last()!.ToDictionary(p => p.Name, p => p.Value.StringValue);
        Assert.Equal(start, parameters["start"]);
        Assert.Equal(end, parameters["end"]);
    }

    [Theory]
    [InlineData("1916-11-01")]
    [InlineData("1917-03-01")]
    public async Task Create_ShouldStillRejectLegacyDate_WhenLisbonMidnightIsAmbiguousOrInvalid(string date)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync("/Expense", new { bookingDate = date, amount = "0", category = "Test" });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task List_ShouldFilterBookingInstantsAndOrderTies_WhenOctoberCrossesDstAndCreatorsDiffer()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        SeedOctoberExpenses(factory.Postgres);
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/Expense?category=startup&month=2026-10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expenses = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("expenses").EnumerateArray().ToArray();
        Assert.Equal(new[] { 11, 10, 4, 3, 2 }, expenses.Select(e => e.GetProperty("id").GetInt32()));
        AssertExactValuesAndSharedVisibility(expenses);
        AssertReadFields(expenses);
        AssertOctoberQuery(factory.Postgres);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    private static void SeedOctoberExpenses(FakePostgresDataApiClient postgres)
    {
        postgres.ExpenseCategoryIds["startup"] = 7;
        postgres.SeedExpense(1, "2026-09-30T22:59:59.999999Z", "1", "startup");
        postgres.SeedExpense(2, "2026-09-30T23:00:00Z", "0", "startup");
        postgres.SeedExpense(3, "2026-10-25T00:30:00Z", "0.0000000000000000000000000001", "startup");
        postgres.SeedExpense(4, "2026-10-25T01:30:00Z", "-123456789.1234567890123456789", "startup", "agent@example.com");
        postgres.SeedExpense(10, "2026-10-25T01:30:00Z", "79228162514264337593543950335", "startup");
        postgres.SeedExpense(11, "2026-10-31T23:59:59.999999Z", "-1", "startup");
        postgres.SeedExpense(12, "2026-11-01T00:00:00Z", "1", "startup");
        postgres.SeedExpense(13, "2026-10-31T23:59:59Z", "1", "Restaurants");
    }

    private static void AssertExactValuesAndSharedVisibility(JsonElement[] expenses)
    {
        Assert.Equal(new[] { "-1", "79228162514264337593543950335", "-123456789.1234567890123456789", "0.0000000000000000000000000001", "0" },
            expenses.Select(e => e.GetProperty("amount").GetString()));
        Assert.Equal("2026-10-25T01:30:00+00:00", expenses[2].GetProperty("bookingDate").GetString());
        Assert.Equal("2026-10-25T01:30:00+01:00", expenses[3].GetProperty("bookingDate").GetString());
        Assert.Contains(expenses, e => e.GetProperty("creatorEmail").GetString() == "human@example.com");
        Assert.Contains(expenses, e => e.GetProperty("creatorEmail").GetString() == "agent@example.com");
    }

    private static void AssertReadFields(JsonElement[] expenses)
    {
        Assert.All(expenses, e =>
        {
            Assert.Equal("startup", e.GetProperty("category").GetString());
            Assert.Equal("EUR", e.GetProperty("currency").GetString());
            Assert.Equal("Original purchase", e.GetProperty("description").GetString());
            Assert.Equal("2027-02-01T10:00:00+00:00", e.GetProperty("creationDate").GetString());
            Assert.Equal(JsonValueKind.Null, e.GetProperty("updateDate").ValueKind);
            Assert.Equal(new[] { "amount", "bookingDate", "category", "creationDate", "creatorEmail", "currency", "description", "id", "updateDate" },
                e.EnumerateObject().Select(p => p.Name).Order());
        });
    }

    private static void AssertOctoberQuery(FakePostgresDataApiClient postgres)
    {
        var parameters = postgres.ExecutedParameters.Last()!.ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal("2026-09-30T23:00:00.0000000+00:00", parameters["start"].StringValue);
        Assert.Equal("2026-11-01T00:00:00.0000000+00:00", parameters["end"].StringValue);
        Assert.Equal(7L, parameters["category_id"].LongValue);
        Assert.Equal(new[] { "category_id", "end", "start" }, parameters.Keys.Order());
        Assert.DoesNotContain("LIMIT", postgres.ExecutedSql.Last(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("creator_email =", postgres.ExecutedSql.Last(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task List_ShouldFailExplicitlyWithoutPartialArray_WhenDataApiFails(bool sizeLimit, int failingCall)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        factory.Postgres.ExecuteExceptionAtCall = failingCall;
        factory.Postgres.ExecuteException = sizeLimit
            ? new Amazon.RDSDataService.Model.DatabaseErrorException("Database response exceeded size limit")
            : new Amazon.RDSDataService.AmazonRDSDataServiceException("Unavailable");
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/Expense?category=Test&month=2026-10");

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("complete", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    [Fact]
    public async Task List_ShouldSerializeCompleteResult_WhenFakeProviderReturnsLargeDescription()
    {
        // The fake bypasses real Data API transport limits; this verifies MVC only.
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        var description = new string('x', 2_000_001);
        factory.Postgres.SeedExpense(1, "2026-10-10T10:00:00Z", "0", "Test", description: description);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/Expense?category=Test&month=2026-10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.True(body.Length > 2_000_000);
        using var document = JsonDocument.Parse(body);
        var expense = Assert.Single(document.RootElement.GetProperty("expenses").EnumerateArray());
        Assert.Equal(1, expense.GetProperty("id").GetInt32());
        Assert.Equal(description, expense.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?month=2026-10")]
    [InlineData("?category=Test")]
    [InlineData("?category=&month=2026-10")]
    [InlineData("?category=%20%20&month=2026-10")]
    [InlineData("?category=Test&month=")]
    [InlineData("?category=Test&month=2026-1")]
    [InlineData("?category=Test&month=2026-00")]
    [InlineData("?category=Test&month=2026-13")]
    [InlineData("?category=Test&month=0000-10")]
    [InlineData("?category=Test&month=2026-10-01")]
    [InlineData("?category=Test&month=%202026-10")]
    [InlineData("?category=Test&month=garbage")]
    public async Task List_ShouldReturnBadRequestWithoutTransport_WhenRequiredFilterInvalid(string query)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/Expense{query}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    [Fact]
    public async Task List_ShouldReturnEmptyArray_WhenKnownCategoryHasNoExpenses()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/Expense?category=Test&month=2026-10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(body.RootElement.GetProperty("expenses").EnumerateArray());
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }
}
