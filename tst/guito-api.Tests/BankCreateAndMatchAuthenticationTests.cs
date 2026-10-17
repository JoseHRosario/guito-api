using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

public class BankCreateAndMatchAuthenticationTests
{
    private const string Path = "/BankTransaction/9223372036854775807/create-and-match";
    private const string OperationId = "11111111-2222-3333-4444-555555555555";

    private static object Request() => new
    {
        operationId = OperationId, bookingDate = "2026-07-01", amount = "0", category = "Groceries",
        description = "Authenticated purchase", currency = "EUR", creatorEmail = "forged@example.com"
    };

    private static CustomWebApplicationFactory Factory()
    {
        var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true, UseRealBankTransactionRepository = true };
        factory.Postgres.HasBankReviewSchema = true;
        factory.Postgres.NextTransactionId = "authenticated-create-match";
        factory.Postgres.LinkedTransactions.Add(long.MaxValue, (null, "needsReview"));
        return factory;
    }

    private static WebApplicationFactory<Program> Authenticated(CustomWebApplicationFactory factory, ReportingTokenFixture? transport = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppConfiguration:Authentication:ValidateApiKey"] = "true",
                ["AppConfiguration:Authentication:ValidateIdToken"] = "true",
                ["AppConfiguration:Authentication:OAuthAudience"] = "human-ui-client",
                ["AppConfiguration:Authentication:AllowedLogins:0"] = "editor@example.com"
            }));
            if (transport is not null) builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(_ => new HttpClient(transport, disposeHandler: false));
                services.AddSingleton(sp => GoogleCertificateTransportFixture.Settings(sp.GetRequiredService<HttpClient>()));
            });
        });

    [Theory]
    [InlineData(null, 401)]
    [InlineData("wrong-key", 403)]
    public async Task CreateAndMatch_ShouldDenyBothRoutes_WhenAgentCredentialsAreAbsentOrInvalidAsync(string? key, int status)
    {
        // Arrange
        using var factory = Factory();
        using var host = Authenticated(factory);
        using var client = host.CreateClient();
        if (key is not null) client.DefaultRequestHeaders.Add("X-Api-Key", key);

        // Act
        using var post = await client.PostAsJsonAsync(Path, Request());
        using var get = await client.GetAsync(Path + "/" + OperationId);

        // Assert
        Assert.Equal((HttpStatusCode)status, post.StatusCode);
        Assert.Equal((HttpStatusCode)status, get.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task CreateAndMatch_ShouldPreserveAgentAttribution_WhenAgentKeyIsValidAsync()
    {
        // Arrange
        using var factory = Factory();
        using var host = Authenticated(factory);
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-agent-key");

        // Act
        using var response = await client.PostAsJsonAsync(Path, Request());

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expense = await client.GetFromJsonAsync<JsonElement>("/Expense/1");
        Assert.Equal("xunga.meireles@gmail.com", expense.GetProperty("creatorEmail").GetString());
        using var get = await client.GetAsync(Path + "/" + OperationId);
        Assert.Equal(await response.Content.ReadAsStringAsync(), await get.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreateAndMatch_ShouldUseHumanIdentity_WhenGoogleTokenIsValidAsync()
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = Factory();
        using var host = Authenticated(factory, transport);
        using var client = host.CreateClient();
        SetHumanCredentials(client, transport);

        // Act
        using var response = await client.PostAsJsonAsync(Path, Request());

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expense = await client.GetFromJsonAsync<JsonElement>("/Expense/1");
        Assert.Equal("editor@example.com", expense.GetProperty("creatorEmail").GetString());
    }

    [Fact]
    public async Task CreateAndMatch_ShouldAllowSharedRecovery_WhenAgentRetriesHumanOperationAsync()
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        using var factory = Factory();
        using var host = Authenticated(factory, transport);
        using var client = host.CreateClient();
        SetHumanCredentials(client, transport);
        using var first = await client.PostAsJsonAsync(Path, Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Remove("x-google-idtoken");
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-agent-key");

        // Act
        using var retry = await client.PostAsJsonAsync(Path, Request());

        // Assert
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
    }

    private static void SetHumanCredentials(HttpClient client, ReportingTokenFixture transport)
    {
        var token = transport.Token(payload =>
        {
            payload["aud"] = "human-ui-client";
            payload["email"] = "editor@example.com";
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("x-google-idtoken", token);
    }

    [Fact]
    public async Task ExpenseCreate_ShouldKeepNormalBehavior_WhenNoBankOperationIsRequestedAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();

        // Act
        using var created = await client.PostAsJsonAsync("/Expense", new
        {
            bookingDate = "2026-07-01", amount = "-10.123456789", category = "Groceries", description = "Manual purchase"
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(1, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32());
        var expense = await client.GetFromJsonAsync<JsonElement>("/Expense/1");
        Assert.Equal("-10.123456789", expense.GetProperty("amount").GetString());
        var bank = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal("needsReview", bank.GetProperty("reviewStatus").GetString());
        using var receipt = await client.GetAsync(Path + "/" + OperationId);
        Assert.Equal(HttpStatusCode.NotFound, receipt.StatusCode);
    }
}
