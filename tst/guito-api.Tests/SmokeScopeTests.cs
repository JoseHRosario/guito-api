using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Tests;

/// <summary>
/// ADR-0009 smoke-scope acceptance: list-latest and match are pure reads (no
/// dummy-row append), create exposes the integer Expense Id, the X-Sheet-Scope
/// header redirects writes/reads to the Smoke Test tab, and DELETE /Smoke/{id}
/// is an id-scoped verify-then-delete restricted to that tab. Each test uses a
/// fresh factory so its fake handler starts clean.
/// </summary>
public class SmokeScopeTests
{
    [Fact]
    public async Task Latest_ShouldKeepAbsoluteRowIdentity_WhenBlankPositionsAreSkipped()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        factory.SheetsHandler.LatestValuesJson = """
            [[], ["2026-09-19", "", "", "45.00", "Groceries", "Groceries", "jose@example.test"],
             ["2026-09-18", "", "", "8.90", "Bus", "Transport", "jose@example.test"]]
            """;
        using var client = factory.CreateClient();
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<GuitoApi.Configuration.AppConfigurationOptions>>().Value;
        var firstRow = TestSheetsConfiguration.FirstDataRow(options);

        // Act
        var response = await client.GetAsync("/Expense/latest/10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var latest = await response.Content.ReadFromJsonAsync<ExpenseListLatest>();
        Assert.Equal(new[] { firstRow + 1, firstRow + 2 }, latest!.Expenses.Select(expense => expense.Id));
    }

    [Fact]
    public async Task CreateExpense_ShouldRefuseLossyTimestamp_WhenSheetsStillAuthoritative()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        var expense = ValidExpense();
        expense.BookingDate = DateTimeOffset.Parse("2026-09-21T14:00:00+01:00");

        // Act
        var response = await client.PostAsJsonAsync("/Expense", expense);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.SheetsHandler.AppendBodies);
    }

    [Fact]
    public async Task ListLatest_ShouldExposeAbsoluteIdentity_WhenReadingTailSlice()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<GuitoApi.Configuration.AppConfigurationOptions>>().Value;
        var lastRow = TestSheetsConfiguration.FirstDataRow(options) + 2;

        // Act
        var latest = await client.GetFromJsonAsync<ExpenseListLatest>("/Expense/latest/1");

        // Assert
        Assert.Equal(lastRow, latest!.Expenses[0].Id);
        Assert.Equal("EUR", latest.Expenses[0].Currency);
        Assert.Equal(TimeSpan.FromHours(1), latest.Expenses[0].BookingDate!.Value.Offset);
        Assert.Null(latest.Expenses[0].CreationDate);
    }

    private static ExpenseCreate ValidExpense() => new()
    {
        BookingDate = DateTimeOffset.Parse("2026-09-21T00:00:00+01:00"),
        Amount = 5.00m,
        Description = "smoke espresso",
        Category = "Restaurants",
    };

    [Fact]
    public async Task ListLatestAsync_ShouldNotAppend_WhenReadingLatestExpenses()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/expense/latest/5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(factory.SheetsHandler.AppendRanges);
    }

    [Fact]
    public async Task Match_ShouldNotAppend_WhenMatchingExpensesWithTransactions()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/expense/match");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(factory.SheetsHandler.AppendRanges);
    }

    [Fact]
    public async Task CreateExpense_ShouldReturnIntegerId_WhenExpenseIsValid()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/expense", ValidExpense());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ExpenseCreated>();
        Assert.Equal(FakeSheetsHttpHandler.NextRowIndex, created!.Id);
    }

    [Fact]
    public async Task CreateExpense_ShouldAppendToSmokeRange_WhenSmokeScopeHeaderPresent()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Sheet-Scope", "smoke");

        var response = await client.PostAsJsonAsync("/expense", ValidExpense());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(factory.SheetsHandler.AppendRanges, r => r.StartsWith("Smoke Test!", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListLatestAsync_ShouldReadSmokeRange_WhenSmokeScopeHeaderPresent()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Sheet-Scope", "smoke");

        var response = await client.GetAsync("/expense/latest/3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(factory.SheetsHandler.ReadRanges, r => r.StartsWith("Smoke Test!", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-an-integer")]
    [InlineData("2147483648")]
    public async Task DeleteSmoke_ShouldReturnBadRequest_WhenIdIsNotInt32(string id)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        // Act
        var response = await client.DeleteAsync($"/Smoke/{id}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.SheetsHandler.DeleteDimensions);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    [Fact]
    public async Task DeleteSmoke_ShouldDimensionDeleteRow_WhenRowExists()
    {
        using var factory = new CustomWebApplicationFactory();
        factory.SheetsHandler.SmokeRowExists = true;
        var client = factory.CreateClient();

        var response = await client.DeleteAsync("/Smoke/5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delete = Assert.Single(factory.SheetsHandler.DeleteDimensions);
        Assert.Equal(FakeSheetsHttpHandler.SmokeSheetId, delete.SheetId);
        Assert.Equal(4, delete.StartIndex); // API row 5 ⇒ 0-based dimension index 4
        Assert.Equal(5, delete.EndIndex);
    }

    [Fact]
    public async Task DeleteSmoke_ShouldReturnNotFound_WhenRowIsAlreadyGone()
    {
        using var factory = new CustomWebApplicationFactory();
        factory.SheetsHandler.SmokeRowExists = false;
        var client = factory.CreateClient();

        var response = await client.DeleteAsync("/Smoke/5");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.SheetsHandler.DeleteDimensions);
    }

    [Fact]
    public async Task DeleteSmoke_ShouldReturnNotFound_WhenIdIsBelowFirstDataRow()
    {
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // Smoke Test tab anchors at B5; ids below the anchor can never exist.
        var response = await client.DeleteAsync("/Smoke/2");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.SheetsHandler.DeleteDimensions);
    }
}
