using System.Net;
using System.Text;
using GuitoApi.Infrastructure.Postgres;

namespace GuitoApi.Tests;

public class RetiredSheetsBoundaryTests
{
    [Fact]
    public async Task Request_ShouldRejectEmptyRetiredHeader_WhenTransportPreservesPresence()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        // Act: HttpClient strips empty header values; send through the same HTTP pipeline directly.
        var response = await factory.Server.SendAsync(context =>
        {
            context.Request.Method = "DELETE";
            context.Request.Path = "/Expense/1";
            context.Request.Headers["X-Sheet-Scope"] = "";
        });

        // Assert
        Assert.Equal(400, response.Response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

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
    [InlineData("POST", "/Expense", "smoke")]
    [InlineData("GET", "/Expense/latest/1", "smoke")]
    [InlineData("DELETE", "/Smoke/1", "smoke")]
    [InlineData("PUT", "/Expense/1", "personal")]
    [InlineData("DELETE", "/Expense/1", " ")]
    [InlineData("POST", "/Expense", "unknown")]
    public async Task Request_ShouldRejectRetiredHeaderBeforePersistence_WhenHeaderIsPresent(string method, string path, string value)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.TryAddWithoutValidation("X-Sheet-Scope", new[] { value });
        request.Content = new StringContent("""{"bookingDate":"2026-10-10","amount":"1.23","category":"Test"}""", Encoding.UTF8, "application/json");

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }
}
