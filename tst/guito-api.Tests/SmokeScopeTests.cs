using System.Net;
using System.Net.Http.Json;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Tests;

/// <summary>
/// ADR-0009 smoke-scope acceptance: list-latest and match are pure reads (no
/// dummy-row append), create exposes the opaque Expense Id, the X-Sheet-Scope
/// header redirects writes/reads to the Smoke Test tab, and DELETE /Smoke/{id}
/// is an id-scoped verify-then-delete restricted to that tab. Each test uses a
/// fresh factory so its fake handler starts clean.
/// </summary>
public class SmokeScopeTests
{
    private static ExpenseCreate ValidExpense() => new()
    {
        Date = new DateTime(2026, 9, 21),
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
    public async Task CreateExpense_ShouldReturnOpaqueId_WhenExpenseIsValid()
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
