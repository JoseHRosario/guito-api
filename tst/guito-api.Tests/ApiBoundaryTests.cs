using System.Net;
using System.Net.Http.Json;
using GuitoApi.DataTransferObjects.Output;
using Microsoft.Extensions.Configuration;

namespace GuitoApi.Tests;

public class ApiBoundaryTests
{
    [Fact]
    public async Task HealthzEndpoint_ShouldReturnOk_WhenCalledWithoutCredentials()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync("/healthz");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("-42.50")]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("42.50")]
    public async Task CreateExpense_ShouldPersistExactAmountAndDescription_WhenExpenseIsValid(string amount)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync("/Expense", new
        {
            bookingDate = "2026-09-21", amount, description = "coffee shop", category = "Restaurants"
        });
        var latest = await client.GetFromJsonAsync<ExpenseListLatest>("/Expense/latest/1");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expense = Assert.Single(latest!.Expenses);
        Assert.Equal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), expense.Amount);
        Assert.Equal("coffee shop", expense.Description);
        Assert.Equal("Restaurants", expense.Category);
        Assert.Equal(new DateTime(2026, 9, 21), expense.BookingDate!.Value.Date);
    }

    [Fact]
    public async Task MatchExpenses_ShouldCombineTransactionsAndExpenses_WhenBothAreAvailable()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        factory.Postgres.SeedExpense(5, "2026-09-21T00:00:00Z", "1.23", "Restaurants");
        using var client = factory.CreateClient();

        // Act
        var result = await client.GetFromJsonAsync<ExpenseMatchList>("/Expense/match");

        // Assert
        Assert.Contains(result!.Matches, match => match.Transaction is not null);
        Assert.Contains(result.Matches, match => match.Expense is not null);
    }

    [Fact]
    public async Task GoogleIdTokenMiddleware_ShouldRejectRequest_WhenIdTokenHeaderIsMissing()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["AppConfiguration:Authentication:ValidateIdToken"] = "true" })));
        using var client = configured.CreateClient();

        // Act
        using var response = await client.GetAsync("/Expense/latest/1");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }
}
