using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

public class ExpenseCutoverBoundaryTests
{
    [Theory]
    [InlineData("/Expense/latest/1")]
    [InlineData("/Expense/history")]
    public async Task Expense_ShouldRefuseReadsWithoutTransport_WhenProductionApprovalMissing(string path)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { Environment = "Production", ExpenseStore = "Postgres" };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-agent-key");

        // Act
        var response = await client.GetAsync(path);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.ReadRanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expense_ShouldKeepSheetsAndHistoryGuard_WhenProductionStoreNotSelected(bool approved)
    {
        // Arrange
        using var factory = CreateProductionFactory("Sheets");
        using var host = CreateCutoverHost(factory, approved);
        using var client = CreateClient(host);

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
    public async Task CreateExpense_ShouldPauseBeforeTransportWrites_WhenPersonalWritesPaused(string store, string path)
    {
        // Arrange
        using var factory = CreateProductionFactory(store);
        using var host = CreatePausedHost(factory);
        using var client = CreateClient(host);

        // Act
        var paused = await CreateExpenseAsync(client, path);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, paused.StatusCode);
        Assert.Equal(503, (await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Empty(factory.SheetsHandler.AppendBodies);
    }

    [Theory]
    [InlineData("Sheets", "/Expense")]
    [InlineData("Postgres", "/expense/")]
    public async Task CreateExpense_ShouldRequireAuthentication_WhenPersonalWritesPaused(string store, string path)
    {
        // Arrange
        using var factory = CreateProductionFactory(store);
        using var host = CreatePausedHost(factory);
        using var client = CreateClient(host, authenticated: false);

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
    public async Task Read_ShouldRetainAvailability_WhenPersonalWritesPaused(string store, string path, HttpStatusCode expected)
    {
        // Arrange
        using var factory = CreateProductionFactory(store);
        using var host = CreatePausedHost(factory);
        using var client = CreateClient(host);

        // Act
        var response = await client.GetAsync(path);

        // Assert
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("Sheets")]
    [InlineData("Postgres")]
    public async Task Smoke_ShouldKeepCrudIsolatedInSheets_WhenPersonalWritesPaused(string store)
    {
        // Arrange
        using var factory = CreateProductionFactory(store);
        using var host = CreatePausedHost(factory);
        using var client = CreateClient(host);
        client.DefaultRequestHeaders.Add("X-Sheet-Scope", "smoke");

        // Act
        var created = await CreateExpenseAsync(client, "/Expense");
        var latest = await client.GetAsync("/Expense/latest/1");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var deleted = await client.DeleteAsync($"/Smoke/{id}");

        // Assert
        AssertSmokeCrud(factory, host, created, latest, deleted);
    }

    private static CustomWebApplicationFactory CreateProductionFactory(string store) =>
        new() { Environment = "Production", ExpenseStore = store, Postgres = { PersistExpenses = true } };

    private static WebApplicationFactory<Program> CreatePausedHost(CustomWebApplicationFactory factory) =>
        CreateCutoverHost(factory, approved: true, paused: true);

    private static WebApplicationFactory<Program> CreateCutoverHost(CustomWebApplicationFactory factory, bool approved, bool paused = false) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<AppConfigurationOptions>(options =>
            {
                options.ExpenseCutover.ProductionPostgresApproved = approved;
                options.ExpenseCutover.WritesPaused = paused;
            })));

    private static HttpClient CreateClient(WebApplicationFactory<Program> host, bool authenticated = true)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (authenticated)
            client.DefaultRequestHeaders.Add("X-Api-Key", "test-agent-key");
        return client;
    }

    private static Task<HttpResponseMessage> CreateExpenseAsync(HttpClient client, string path) =>
        client.PostAsJsonAsync(path, new { bookingDate = "2026-10-10", amount = 1, category = "Test" });

    private static void AssertSmokeCrud(CustomWebApplicationFactory factory, WebApplicationFactory<Program> host,
        HttpResponseMessage created, HttpResponseMessage latest, HttpResponseMessage deleted)
    {
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Single(factory.SheetsHandler.AppendBodies);
        var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppConfigurationOptions>>().Value;
        Assert.Equal(options.Googlesheets.ExpensesSmokeRange, Assert.Single(factory.SheetsHandler.AppendRanges));
        Assert.Equal(FakeSheetsHttpHandler.SmokeSheetId, Assert.Single(factory.SheetsHandler.DeleteDimensions).SheetId);
    }

    [Fact]
    public async Task Expense_ShouldSwitchReadsWritesAndHistoryTogether_WhenProductionApproved()
    {
        // Arrange
        using var factory = CreateProductionFactory("Postgres");
        using var host = CreateCutoverHost(factory, approved: true);
        using var client = CreateClient(host);

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
