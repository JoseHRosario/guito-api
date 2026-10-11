using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Tests;

public class ApiBoundaryTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ApiBoundaryTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task HealthzEndpoint_ShouldReturnOk_WhenCalledWithoutCredentials()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateExpense_ShouldAppendExactAmount_WhenAmountIsNegative()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/expense", new ExpenseCreate
        {
            BookingDate = DateTimeOffset.Parse("2026-10-02T00:00:00+01:00"),
            Amount = -42.50m,
            Description = "negative test",
            Category = "Restaurants",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(Assert.Single(factory.SheetsHandler.AppendBodies));
        Assert.Equal(-42.50m, body.RootElement.GetProperty("values")[0][3].GetDecimal());
    }

    [Fact]
    public async Task CreateExpense_ShouldAppendExactAmount_WhenAmountIsZero()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/expense", new ExpenseCreate
        {
            BookingDate = DateTimeOffset.Parse("2026-10-02T00:00:00+01:00"),
            Amount = 0m,
            Description = "zero test",
            Category = "Restaurants",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(Assert.Single(factory.SheetsHandler.AppendBodies));
        Assert.Equal(0m, body.RootElement.GetProperty("values")[0][3].GetDecimal());
    }

    [Fact]
    public async Task CreateExpense_ShouldAppendPayload_WhenAmountIsInclusiveLowerBound()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/expense", new ExpenseCreate
        {
            BookingDate = DateTimeOffset.Parse("2026-10-02T00:00:00+01:00"),
            Amount = 0.01m,
            Description = "minimum amount",
            Category = "Restaurants",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("0.01", factory.SheetsHandler.AppendBodies.Last());
    }

    [Fact]
    public async Task CreateExpense_ShouldAppendPayloadToSpreadsheet_WhenExpenseIsValid()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/expense", new ExpenseCreate
        {
            BookingDate = DateTimeOffset.Parse("2026-09-21T00:00:00+01:00"),
            Amount = 42.50m,
            Description = "coffee shop",
            Category = "Restaurants",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sheets = _factory.SheetsHandler;
        var options = _factory.Services.CreateScope().ServiceProvider
            .GetRequiredService<IOptions<AppConfigurationOptions>>().Value;
        var dateColumn = options.Googlesheets.ExpensesDateRange.Split('!')[0]; // e.g. ExpensesAux or Expenses

        var appendBody = sheets.AppendBodies.Last();
        Assert.Contains("Coffee Shop", appendBody);
        Assert.Contains("42.5", appendBody);
        Assert.Contains("Restaurants", appendBody);
        Assert.Contains("2026-09-21", appendBody);
        // The year/month formulas are written to the appended row
        Assert.Contains($"{dateColumn}!C{FakeSheetsHttpHandler.NextRowIndex}", sheets.UpdateRanges.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateExpense_ShouldKeepLegacyDateOnlyContract_WhenProductionUsesDefaultSheets(bool amountAsString)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { Environment = "Production" };
        factory.SheetsHandler.LatestValuesJson = """
            [["2026-07-02", "", "", "42.50", "Coffee", "Restaurants", "user@example.test"]]
            """;
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-agent-key");
        object amount = amountAsString ? "42.50" : (object)42.50m;

        // Act
        var created = await client.PostAsJsonAsync("/Expense", new { bookingDate = "2026-07-02", amount });
        var latest = (await client.GetFromJsonAsync<JsonElement>("/Expense/latest/1")).GetProperty("expenses");

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains("2026-07-02", Assert.Single(factory.SheetsHandler.AppendBodies));
        Assert.Empty(factory.Postgres.ExecutedSql);
        var expense = Assert.Single(latest.EnumerateArray());
        Assert.Equal(JsonValueKind.String, expense.GetProperty("amount").ValueKind);
        Assert.Equal(42.50m, decimal.Parse(expense.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(expense.TryGetProperty("amountExact", out _));
        Assert.False(expense.TryGetProperty("date", out _));
        Assert.False(expense.TryGetProperty("occurredAt", out _));
        Assert.False(expense.TryGetProperty("storedOrder", out _));
    }

    [Fact]
    public async Task ListLatestAsync_ShouldReadRowsFromSpreadsheet_WhenExpensesExist()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/expense/latest/10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ExpenseListLatest>();
        Assert.NotNull(result);
        Assert.Equal(3, result!.Expenses.Count);
        var first = result.Expenses[0];
        Assert.Equal(12.50m, first.Amount);
        Assert.Equal("Coffee", first.Description);
        Assert.Equal("Restaurants", first.Category);
        Assert.Equal(new DateTime(2026, 9, 20), first.BookingDate?.Date);
    }

    [Fact]
    public async Task MatchExpensesAsync_ShouldCombineTransactionsAndExpenses_WhenBothAreAvailable()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/expense/match");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ExpenseMatchList>();
        Assert.NotNull(result);
        Assert.NotEmpty(result!.Matches);
        // Canned bank transactions and stored expenses both surface in the match list
        Assert.Contains(result.Matches, m => m.Transaction is not null);
        Assert.Contains(result.Matches, m => m.Expense is not null);
    }

    [Fact]
    public async Task ListAsync_ShouldReadCategoriesFromSpreadsheet_WhenCategoriesExist()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/category");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<CategoryList>();
        Assert.NotNull(result);
        Assert.Equal(["Groceries", "Restaurants", "Transport"], result!.Categories.Select(c => c.Name));
    }

    [Fact]
    public async Task GoogleIdTokenMiddleware_ShouldRejectRequest_WhenIdTokenHeaderIsMissing()
    {
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppConfiguration:Authentication:ValidateIdToken"] = "true",
                }))).CreateClient();

        var response = await client.GetAsync("/expense/latest/1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
