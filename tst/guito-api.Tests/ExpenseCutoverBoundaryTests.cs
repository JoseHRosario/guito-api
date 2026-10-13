using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

public class ExpenseCutoverBoundaryTests
{
    [Fact]
    public async Task Expense_ShouldKeepSheetsAndHistoryGuard_WhenProductionStoreNotSelected()
    {
        // Arrange
        using var factory = CreateProductionFactory("Sheets");
        using var client = CreateClient(factory);

        // Act
        var created = await client.PostAsJsonAsync("/Expense", new { bookingDate = "2026-10-10", amount = 1, category = "Test" });
        var history = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, history.StatusCode);
        Assert.Single(factory.SheetsHandler.AppendBodies);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData("Sheets", "/Expense")]
    [InlineData("Postgres", "/expense/")]
    public async Task CreateExpense_ShouldRequireAuthentication_WhenProductionStoreSelected(string store, string path)
    {
        // Arrange
        using var factory = CreateProductionFactory(store);
        using var client = CreateClient(factory, authenticated: false);

        // Act
        var response = await CreateExpenseAsync(client, path);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.AppendBodies);
    }

    [Theory]
    [InlineData("Sheets", "/Expense/latest/1", HttpStatusCode.OK)]
    [InlineData("Postgres", "/Expense/latest/1", HttpStatusCode.OK)]
    [InlineData("Sheets", "/Category", HttpStatusCode.OK)]
    [InlineData("Postgres", "/Category", HttpStatusCode.OK)]
    [InlineData("Sheets", "/Expense/history", HttpStatusCode.Conflict)]
    [InlineData("Postgres", "/Expense/history", HttpStatusCode.OK)]
    public async Task Read_ShouldRetainAvailability_WhenProductionStoreSelected(string store, string path, HttpStatusCode expected)
    {
        // Arrange
        using var factory = CreateProductionFactory(store);
        using var client = CreateClient(factory);

        // Act
        var response = await client.GetAsync(path);

        // Assert
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("Sheets")]
    [InlineData("Postgres")]
    public async Task Smoke_ShouldKeepCrudIsolatedInSheets_WhenProductionStoreSelected(string store)
    {
        // Arrange
        using var factory = CreateProductionFactory(store);
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Add("X-Sheet-Scope", "smoke");

        // Act
        var created = await CreateExpenseAsync(client, "/Expense");
        var latest = await client.GetAsync("/Expense/latest/1");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var deleted = await client.DeleteAsync($"/Smoke/{id}");

        // Assert
        AssertSmokeCrud(factory, created, latest, deleted);
    }

    private static CustomWebApplicationFactory CreateProductionFactory(string store) =>
        new() { Environment = "Production", ExpenseStore = store, Postgres = { PersistExpenses = true } };

    private static HttpClient CreateClient(WebApplicationFactory<Program> host, bool authenticated = true)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (authenticated)
            client.DefaultRequestHeaders.Add("X-Api-Key", "test-agent-key");
        return client;
    }

    private static Task<HttpResponseMessage> CreateExpenseAsync(HttpClient client, string path) =>
        client.PostAsJsonAsync(path, new { bookingDate = "2026-10-10", amount = 1, category = "Test" });

    private static void AssertSmokeCrud(CustomWebApplicationFactory factory,
        HttpResponseMessage created, HttpResponseMessage latest, HttpResponseMessage deleted)
    {
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Single(factory.SheetsHandler.AppendBodies);
        var options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppConfigurationOptions>>().Value;
        Assert.Equal(options.Googlesheets.ExpensesSmokeRange, Assert.Single(factory.SheetsHandler.AppendRanges));
        Assert.Equal(FakeSheetsHttpHandler.SmokeSheetId, Assert.Single(factory.SheetsHandler.DeleteDimensions).SheetId);
    }

    [Fact]
    public async Task Expense_ShouldSwitchReadsWritesAndHistoryTogether_WhenProductionPostgresSelected()
    {
        // Arrange
        using var factory = CreateProductionFactory("Postgres");
        using var client = CreateClient(factory);

        // Act
        var created = await client.PostAsJsonAsync("/Expense", new { bookingDate = "2026-10-10", amount = "1.23", category = "Test" });
        var latest = await client.GetAsync("/Expense/latest/1");
        var history = await client.GetAsync("/Expense/history");

        // Assert
        await AssertPostgresRoundTripAsync(factory, created, latest, history);
    }

    private static async Task AssertPostgresRoundTripAsync(CustomWebApplicationFactory factory,
        HttpResponseMessage created, HttpResponseMessage latest, HttpResponseMessage history)
    {
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var exported = await history.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, exported.GetProperty("totalCount").GetInt64());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(id, Assert.Single(exported.GetProperty("expenses").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Equal(id, Assert.Single((await latest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("expenses").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Empty(factory.SheetsHandler.AppendBodies);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }
}
