using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace GuitoApi.Tests;

public class PostgresOnlyCompositionTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task Expense_ShouldUsePostgresWithoutPersistenceSettings_WhenRunningInAnyEnvironment(string environment)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { Environment = environment, Postgres = { PersistExpenses = true } };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-agent-key");

        // Act
        using var created = await client.PostAsJsonAsync("/Expense", new { bookingDate = "2026-10-10", amount = "1.23", category = "Test" });
        using var latest = await client.GetAsync("/Expense/latest/1");
        using var history = await client.GetAsync("/Expense/history");

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(id, Assert.Single((await latest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("expenses").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Equal(1, (await history.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalCount").GetInt64());
        Assert.Contains(factory.Postgres.ExecutedSql, sql => sql.StartsWith("INSERT INTO expenses", StringComparison.Ordinal));
    }
}
