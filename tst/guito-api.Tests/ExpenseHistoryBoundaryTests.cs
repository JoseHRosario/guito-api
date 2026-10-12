using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using GuitoApi.Infrastructure.Secrets;

namespace GuitoApi.Tests;

public class ExpenseHistoryBoundaryTests
{
    [Fact]
    public async Task History_ShouldReturnAllPages_WhenHistoryExceedsLatestCap()
    {
        // Arrange
        using var factory = CreateHistoryFactory();
        SeedExpenses(factory, 205);
        using var client = factory.CreateClient();

        // Act
        var pages = new List<JsonElement>();
        for (var index = 0; index < 3; index++)
            pages.Add(await client.GetFromJsonAsync<JsonElement>($"/Expense/history?pageSize=100&pageIndex={index}"));

        // Assert
        Assert.Equal(new[] { 100, 100, 5 }, pages.Select(page => page.GetProperty("expenses").GetArrayLength()));
        Assert.Equal(Enumerable.Range(1, 205).Reverse(), pages.SelectMany(Ids));
        Assert.Equal(new[] { true, true, false }, pages.Select(page => page.GetProperty("hasMore").GetBoolean()));
        Assert.All(pages, page => Assert.Equal(205, page.GetProperty("totalCount").GetInt64()));
        Assert.Equal(3, factory.Postgres.ExecutedSql.Count);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(100, 2147483647)]
    public async Task History_ShouldReturnEmptyPageWithTotal_WhenIndexIsBeyondEnd(int pageSize, int pageIndex)
    {
        // Arrange
        using var factory = CreateHistoryFactory();
        SeedExpenses(factory, 2);
        using var client = factory.CreateClient();

        // Act
        var page = await client.GetFromJsonAsync<JsonElement>($"/Expense/history?pageSize={pageSize}&pageIndex={pageIndex}");

        // Assert
        Assert.Empty(Ids(page));
        Assert.Equal(2, page.GetProperty("totalCount").GetInt64());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(pageIndex, page.GetProperty("pageIndex").GetInt32());
        Assert.Equal((long)pageSize * pageIndex, Assert.Single(factory.Postgres.ExecutedParameters)!.Single(p => p.Name == "offset").Value.LongValue);
    }

    [Theory]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=-1")]
    [InlineData("pageIndex=-1")]
    [InlineData("pageSize=no")]
    [InlineData("pageIndex=1.5")]
    [InlineData("pageSize=2147483648")]
    [InlineData("pageIndex=2147483648")]
    public async Task History_ShouldRejectInvalidPagination_WhenQueryIsOutsideContract(string query)
    {
        // Arrange
        using var factory = CreateHistoryFactory();
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/Expense/history?{query}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData("Sheets", false)]
    [InlineData("Postgres", true)]
    [InlineData("Sheets", true)]
    public async Task History_ShouldRefuseUnsupportedAuthorityOrScope_WhenRequested(string store, bool smoke)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = store };
        using var client = factory.CreateClient();
        if (smoke) client.DefaultRequestHeaders.Add("X-Sheet-Scope", "smoke");

        // Act
        var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    [Fact]
    public async Task History_ShouldPreserveStoredRepresentation_WhenAmountsExceedDecimalPrecision()
    {
        // Arrange
        using var factory = CreateHistoryFactory();
        SeedExactExpense(factory);
        using var client = factory.CreateClient();

        // Act
        var page = await client.GetFromJsonAsync<JsonElement>("/Expense/history");

        // Assert
        AssertExactExpense(Assert.Single(page.GetProperty("expenses").EnumerateArray()));
    }

    [Fact]
    public async Task History_ShouldReflectNewExpense_WhenPagesAreReadAcrossWrites()
    {
        // Arrange
        using var factory = CreateHistoryFactory();
        SeedExpenses(factory, 2);
        using var client = factory.CreateClient();
        var first = await client.GetFromJsonAsync<JsonElement>("/Expense/history?pageSize=1");

        // Act
        var created = await client.PostAsJsonAsync("/Expense", new { bookingDate = "2026-10-10T18:00:00Z", amount = 1, category = "Test" });
        var next = await client.GetFromJsonAsync<JsonElement>("/Expense/history?pageSize=1&pageIndex=1");
        var replay = await client.GetFromJsonAsync<JsonElement>("/Expense/history?pageSize=1");

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(new[] { 2 }, Ids(first));
        Assert.Equal(new[] { 2 }, Ids(next));
        Assert.Equal(new[] { 3 }, Ids(replay));
        Assert.Equal(3, next.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task History_ShouldOrderOccurrenceBeforeNumericIdentity_WhenDatesAndTiesCrossPages()
    {
        // Arrange
        using var factory = CreateHistoryFactory();
        SeedExpense(factory, 11, "2026-07-01T18:00:00Z");
        SeedExpense(factory, 12, "2026-07-01T09:00:00Z");
        SeedExpense(factory, 2, "2026-07-01T18:00:00Z");
        using var client = factory.CreateClient();

        // Act
        var first = await client.GetFromJsonAsync<JsonElement>("/Expense/history?pageSize=2");
        var next = await client.GetFromJsonAsync<JsonElement>("/Expense/history?pageSize=2&pageIndex=1");

        // Assert
        Assert.Equal(new[] { 11, 2, 12 }, Ids(first).Concat(Ids(next)));
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        Assert.False(next.GetProperty("hasMore").GetBoolean());
    }

    [Theory]
    [InlineData(null, null, HttpStatusCode.Unauthorized)]
    [InlineData("invalid", null, HttpStatusCode.Forbidden)]
    [InlineData(null, "Bearer invalid.jwt.value", HttpStatusCode.Unauthorized)]
    public async Task History_ShouldRejectUnauthorizedCaller_WhenAuthGatesAreEnabled(string? key, string? bearer, HttpStatusCode expected)
    {
        // Arrange
        using var factory = CreateHistoryFactory();
        using var secured = SecureHistoryHost(factory);
        using var client = secured.CreateClient();
        if (key is not null) client.DefaultRequestHeaders.Add("X-Api-Key", key);
        if (bearer is not null) client.DefaultRequestHeaders.Add("Authorization", bearer);

        // Act
        var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task History_ShouldAcceptAgentWithoutHumanToken_WhenAgentKeyIsValid()
    {
        // Arrange
        using var factory = CreateHistoryFactory();
        using var secured = SecureHistoryHost(factory);
        using var client = secured.CreateClient();
        var secrets = await secured.Services.GetRequiredService<ISecretsProvider>().GetAsync();
        client.DefaultRequestHeaders.Add("X-Api-Key", secrets.ApiKeys[0]);

        // Act
        var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task History_ShouldPreserveProductionGuard_WhenPostgresCutoverIsNotApproved()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Environment = "Production" };
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var secrets = await factory.Services.GetRequiredService<ISecretsProvider>().GetAsync();
        client.DefaultRequestHeaders.Add("X-Api-Key", secrets.ApiKeys[0]);

        // Act
        var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData("0.0000")]
    [InlineData("1.2300")]
    [InlineData("-1.2300")]
    [InlineData("0.000000000000000000000000000000000000001")]
    [InlineData("1234567890123456789012345678901234567890")]
    public async Task History_ShouldRetainNumericTextAndNullAudit_WhenStoredAmountHasSignScaleOrExtremeValue(string amount)
    {
        // Arrange
        using var factory = CreateHistoryFactory();
        factory.Postgres.SeedExpense([Text("1"), Text("2026-01-01T00:00:00Z"), Text(amount),
            Text("Test"), Text("Test"), Text("owner@example.test"), Text("2026-01-01T00:00:00Z"), PostgresValue.Null()]);
        using var client = factory.CreateClient();

        // Act
        var page = await client.GetFromJsonAsync<JsonElement>("/Expense/history");

        // Assert
        var expense = Assert.Single(page.GetProperty("expenses").EnumerateArray());
        Assert.Equal(amount, expense.GetProperty("amount").GetString());
        Assert.Equal(JsonValueKind.Null, expense.GetProperty("updateDate").ValueKind);
    }

    [Fact]
    public async Task History_ShouldFailWithoutPartialSuccess_WhenDataApiFails()
    {
        // Arrange
        using var factory = CreateHistoryFactory();
        factory.Postgres.ExecuteException = new InvalidOperationException("hermetic provider failure");
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("expenses", await response.Content.ReadAsStringAsync());
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> SecureHistoryHost(CustomWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfiguration:Authentication:ValidateApiKey"] = "true",
                ["AppConfiguration:Authentication:ValidateIdToken"] = "true"
            })));

    private static CustomWebApplicationFactory CreateHistoryFactory() =>
        new() { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };

    private static IEnumerable<int> Ids(JsonElement page) =>
        page.GetProperty("expenses").EnumerateArray().Select(expense => expense.GetProperty("id").GetInt32());

    private static void SeedExpenses(CustomWebApplicationFactory factory, int count)
    {
        for (var id = 1; id <= count; id++) SeedExpense(factory, id, "2026-10-10T09:00:00Z");
    }

    private static void SeedExpense(CustomWebApplicationFactory factory, int id, string bookingDate) =>
        factory.Postgres.SeedExpense([Text(id.ToString()), Text(bookingDate), Text("1.00"), Text("Duplicate"),
            Text("Test"), Text("user@example.test"), Text("2026-01-01T00:00:00Z"), PostgresValue.Null()]);

    private static void SeedExactExpense(CustomWebApplicationFactory factory) =>
        factory.Postgres.SeedExpense([Text("2147483647"), Text("2026-07-01T23:30:00.123456Z"),
            Text("-123456789012345678901234567890.1234567890123456789012345678900"), Text("Café"),
            Text("Original category"), Text("original@example.test"), Text("2026-01-01T00:00:00Z"),
            Text("2026-07-02T10:00:00.654321Z")]);

    private static void AssertExactExpense(JsonElement expense)
    {
        Assert.Equal(2147483647, expense.GetProperty("id").GetInt32());
        Assert.Equal("-123456789012345678901234567890.1234567890123456789012345678900", expense.GetProperty("amount").GetString());
        Assert.Equal("EUR", expense.GetProperty("currency").GetString());
        Assert.Equal("2026-07-02T00:30:00.123456+01:00", expense.GetProperty("bookingDate").GetString());
        Assert.Equal("2026-01-01T00:00:00+00:00", expense.GetProperty("creationDate").GetString());
        Assert.Equal("2026-07-02T11:00:00.654321+01:00", expense.GetProperty("updateDate").GetString());
        Assert.Equal("Café", expense.GetProperty("description").GetString());
        Assert.Equal("Original category", expense.GetProperty("category").GetString());
        Assert.Equal("original@example.test", expense.GetProperty("creatorEmail").GetString());
    }

    private static PostgresValue Text(string value) => PostgresValue.FromString(value);

    [Fact]
    public async Task History_ShouldReturnDefaultEmptyPage_WhenNoExpensesExist()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, page.GetProperty("pageIndex").GetInt32());
        Assert.Equal(0, page.GetProperty("totalCount").GetInt64());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
        Assert.Empty(page.GetProperty("expenses").EnumerateArray());
        Assert.Single(factory.Postgres.ExecutedSql);
    }
}
