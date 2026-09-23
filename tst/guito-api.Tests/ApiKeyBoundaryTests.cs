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
    public async Task Valid_value_is_accepted()
    {
        var client = ClientWith(gate: true, headerValue: StoredValue);
        var response = await client.GetAsync("/expense/latest/1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Missing_value_is_unauthorized()
    {
        var client = ClientWith(gate: true);
        var response = await client.GetAsync("/expense/latest/1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_value_is_unauthorized()
    {
        var unknown = "xxx" + StoredValue[3..];
        var client = ClientWith(gate: true, headerValue: unknown);
        var response = await client.GetAsync("/expense/latest/1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Healthz_is_public()
    {
        var client = ClientWith(gate: true);
        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_expense_with_valid_value_appends_to_spreadsheet()
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
    public async Task Disabled_gate_keeps_endpoints_open()
    {
        var client = ClientWith(gate: false);
        var response = await client.GetAsync("/expense/latest/1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Valid_agent_key_bypasses_google_idtoken_gate()
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
    public async Task Healthz_is_public_even_with_google_gate_on()
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
    public async Task Secrets_failure_is_500_not_bypass()
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
        public Task<SecretsPayload> GetAsync() => throw new InvalidOperationException("secrets unavailable");
    }
}
