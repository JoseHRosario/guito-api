using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using GuitoApi.Configuration;
using GuitoApi.Services;
using GuitoApi.DataTransferObjects.Input;

namespace GuitoApi.Tests;

/// <summary>
/// HTTP-boundary tests for the agent key path (ADR-0003, issues #3/#4):
/// a valid X-Api-Key is accepted, missing/unknown keys are rejected.
/// The valid header value comes from FakeSecretsProvider via DI — no literals here.
/// </summary>
public class ApiKeyBoundaryTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ApiKeyBoundaryTests(CustomWebApplicationFactory factory) => _factory = factory;

    private string StoredValue => _factory.Services.CreateScope().ServiceProvider
        .GetRequiredService<ISecretsProvider>().GetAsync().Result.ApiKeys[0];

    private HttpClient ClientWith(bool gate, string? headerValue = null)
    {
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppConfiguration:Authentication:ValidateApiKey"] = gate.ToString(),
                }))).CreateClient();
        if (headerValue is not null)
            client.DefaultRequestHeaders.Add("X-Api-Key", headerValue);
        return client;
    }

    [Fact]
    public async Task ApiKeyMiddleware_ShouldAllowEndpoint_WhenHeaderValueIsValid()
    {
        var client = ClientWith(gate: true, headerValue: StoredValue);
        var response = await client.GetAsync("/expense/latest/1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyMiddleware_ShouldReturnUnauthorized_WhenHeaderValueIsMissing()
    {
        var client = ClientWith(gate: true);
        var response = await client.GetAsync("/expense/latest/1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyMiddleware_ShouldPassGoogleCredentialsThrough_WhenOnlyHumanPathHeadersArePresent()
    {
        // Human path (ADR-0003): a request presenting Google credentials is gated
        // by GoogleIdTokenMiddleware, not by the agent-key gate. The agent gate must
        // not demand an X-Api-Key from it — a Bearer request without an agent key
        // fails in the Google middleware ("Missing IdentityToken"), never with
        // "Missing API key".
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppConfiguration:Authentication:ValidateApiKey"] = true.ToString(),
                    ["AppConfiguration:Authentication:ValidateIdToken"] = true.ToString(),
                }))).CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer some.jwt.value");

        var response = await client.GetAsync("/expense/latest/1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Missing API key", body);
    }

    [Fact]
    public async Task ApiKeyMiddleware_ShouldReturnForbidden_WhenHeaderValueIsUnknown()
    {
        // Status convention: missing credentials → 401, rejected credentials → 403.
        var unknown = "xxx" + StoredValue[3..];
        var client = ClientWith(gate: true, headerValue: unknown);

        var response = await client.GetAsync("/expense/latest/1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("Invalid API key", body); // produced by ApiKeyMiddleware, not another gate
    }

    [Fact]
    public async Task ApiKeyMiddleware_ShouldAllowHealthz_WhenNoCredentialsArePresent()
    {
        var client = ClientWith(gate: true);
        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateExpense_ShouldAppendRowToSpreadsheet_WhenApiKeyIsValid()
    {
        var client = ClientWith(gate: true, headerValue: StoredValue);
        var response = await client.PostAsJsonAsync("/expense", new ExpenseCreate
        {
            Date = new DateTime(2026, 9, 23),
            Amount = 9.80m,
            Description = "espresso",
            Category = "Restaurants",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Espresso", _factory.SheetsHandler.AppendBodies.Last());
    }

    [Fact]
    public async Task ApiKeyMiddleware_ShouldLeaveEndpointsOpen_WhenValidationIsDisabled()
    {
        var client = ClientWith(gate: false);
        var response = await client.GetAsync("/expense/latest/1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GoogleIdTokenMiddleware_ShouldSkipTokenCheck_WhenAgentKeyWasAccepted()
    {
        // Paths are independent (ADR-0003): a validated agent key must reach
        // the endpoint without a Google ID token, even when ValidateIdToken=true.
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppConfiguration:Authentication:ValidateApiKey"] = true.ToString(),
                    ["AppConfiguration:Authentication:ValidateIdToken"] = true.ToString(),
                }))).CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", StoredValue);

        var response = await client.GetAsync("/expense/latest/1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GoogleIdTokenMiddleware_ShouldAllowHealthz_WhenNoTokenIsPresent()
    {
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppConfiguration:Authentication:ValidateApiKey"] = true.ToString(),
                    ["AppConfiguration:Authentication:ValidateIdToken"] = true.ToString(),
                }))).CreateClient();

        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyMiddleware_ShouldReturn500_WhenSecretsCannotBeLoaded()
    {
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppConfiguration:Authentication:ValidateApiKey"] = true.ToString(),
                }));
            b.ConfigureTestServices(services =>
            {
                var descriptor = services.Single(d => d.ServiceType == typeof(ISecretsProvider));
                services.Remove(descriptor);
                services.AddScoped<ISecretsProvider>(_ => new ThrowingSecretsProvider());
            });
        }).CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "any-value");

        var response = await client.GetAsync("/expense/latest/1");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private sealed class ThrowingSecretsProvider : ISecretsProvider
    {
        public Task<SecretsPayload> GetAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("secrets unavailable");
    }
}
