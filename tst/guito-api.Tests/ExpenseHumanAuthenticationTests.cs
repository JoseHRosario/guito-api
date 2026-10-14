using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

public class ExpenseHumanAuthenticationTests
{
    [Theory]
    [InlineData("GET", 200)]
    [InlineData("PUT", 200)]
    [InlineData("DELETE", 204)]
    public async Task Expense_ShouldAllowSharedHumanAccess_WhenAnotherIdentityCreatedExpenseAsync(string method, int status)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = CreateFactory();
        using var host = ConfigureHuman(factory, transport);
        using var client = CreateClient(host, transport);
        using var request = Request(method);
        // Act
        using var response = await client.SendAsync(request);
        // Assert
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(1, transport.Requests);
        await AssertResultAsync(client, response, method);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Expense_ShouldDenyWithoutStorageAccess_WhenHumanSignatureIsInvalidAsync(string method)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = CreateFactory();
        using var host = ConfigureHuman(factory, transport);
        using var client = CreateClient(host, transport, wrongSignature: true);
        using var request = Request(method);
        // Act
        using var response = await client.SendAsync(request);
        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Equal(0, factory.Postgres.Commits);
    }

    [Theory]
    [InlineData("aud", "wrong-audience")]
    [InlineData("email", "not-allowed@example.com")]
    [InlineData("iss", "https://untrusted.example")]
    [InlineData("exp", 1L)]
    public async Task Expense_ShouldDenyWithoutStorageAccess_WhenSignedHumanClaimsAreInvalidAsync(string claim, object value)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = CreateFactory();
        using var host = ConfigureHuman(factory, transport);
        using var client = CreateClient(host, transport, change: payload => payload[claim] = value);
        // Act
        using var response = await client.GetAsync("/Expense/7");
        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
        Assert.Equal(0, factory.Postgres.Commits);
    }

    private static CustomWebApplicationFactory CreateFactory()
    {
        var factory = new CustomWebApplicationFactory { Postgres = { PersistExpenses = true } };
        factory.Postgres.NextTransactionId = "human-expense-test";
        factory.Postgres.SeedExpense(7, "2026-07-02T13:23:45Z", "12.3400", "Groceries", "different-creator@example.com");
        factory.Postgres.SeedExpense(8, "2026-07-02T13:23:45Z", "12.3400", "Groceries", "different-creator@example.com");
        return factory;
    }

    private static WebApplicationFactory<Program> ConfigureHuman(CustomWebApplicationFactory factory, ReportingTokenFixture transport) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfiguration:Authentication:ValidateApiKey"] = "true",
                ["AppConfiguration:Authentication:ValidateIdToken"] = "true",
                ["AppConfiguration:Authentication:OAuthAudience"] = "human-ui-client",
                ["AppConfiguration:Authentication:AllowedLogins:0"] = "editor@example.com"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(_ => new HttpClient(transport, disposeHandler: false));
                services.AddSingleton(sp => GoogleCertificateTransportFixture.Settings(sp.GetRequiredService<HttpClient>()));
            });
        });

    private static HttpClient CreateClient(WebApplicationFactory<Program> host, ReportingTokenFixture transport,
        bool wrongSignature = false, Action<Dictionary<string, object>>? change = null)
    {
        var client = host.CreateClient();
        var token = transport.Token(payload =>
        {
            payload["aud"] = "human-ui-client";
            payload["email"] = "editor@example.com";
            change?.Invoke(payload);
        }, wrongSignature);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("x-google-idtoken", token);
        return client;
    }

    private static HttpRequestMessage Request(string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), "/Expense/7");
        if (method != "GET") request.Headers.Add("If-Match", "\"1\"");
        if (method == "PUT") request.Content = JsonContent.Create(new
            { description = "Human correction", category = "Groceries", amount = "12.3400", bookingDate = "2026-07-02T14:23:45+01:00" });
        return request;
    }

    private static async Task AssertResultAsync(HttpClient client, HttpResponseMessage response, string method)
    {
        if (method == "DELETE") Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Expense/7")).StatusCode);
        else
        {
            var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("different-creator@example.com", detail.GetProperty("creatorEmail").GetString());
            Assert.Equal(method == "PUT" ? "2" : "1", detail.GetProperty("revision").GetString());
            Assert.Equal(method == "PUT" ? "Human correction" : "Original purchase", detail.GetProperty("description").GetString());
        }
        var other = await client.GetFromJsonAsync<JsonElement>("/Expense/8");
        Assert.Equal("Original purchase", other.GetProperty("description").GetString());
    }
}
