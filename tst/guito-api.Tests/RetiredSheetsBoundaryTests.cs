using System.Net;

namespace GuitoApi.Tests;

public class RetiredSheetsBoundaryTests
{
    [Fact]
    public async Task Smoke_ShouldReturnNotFoundWithoutPersistence_WhenLegacyRouteIsCalled()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        factory.Postgres.PersistExpenses = true;
        factory.Postgres.SeedExpense(5, "2026-10-10T00:00:00Z", "1.23", "Test");
        using var client = factory.CreateClient();

        // Act
        using var response = await client.DeleteAsync("/Smoke/5");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Expense/5")).StatusCode);
    }

    [Theory]
    [InlineData("smoke")]
    [InlineData("personal")]
    [InlineData("unknown")]
    public async Task Latest_ShouldIgnoreLegacyHeader_WhenPresent(string value)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Sheet-Scope", value);

        // Act
        using var response = await client.GetAsync("/Expense/latest/5");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(factory.Postgres.ExecutedSql);
    }
}
