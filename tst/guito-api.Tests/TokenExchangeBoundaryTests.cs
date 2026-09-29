using System.Net;
using System.Net.Http.Json;
using GuitoApi.Exceptions;
using GuitoApi.Services.Auth;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

/// <summary>
/// HTTP-boundary tests for the server-side token exchange (issue #52):
/// POST /Auth/token is reachable anonymously even with both auth gates on,
/// and delegates to the token-exchange service.
/// </summary>
public class TokenExchangeBoundaryTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TokenExchangeBoundaryTests(CustomWebApplicationFactory factory) => _factory = factory;

    /// <summary>Client with BOTH auth gates on and the exchange service faked via DI.</summary>
    private HttpClient ClientWithGatesOn(out FakeTokenExchangeService fake)
    {
        var exchange = new FakeTokenExchangeService();
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppConfiguration:Authentication:ValidateApiKey"] = true.ToString(),
                    ["AppConfiguration:Authentication:ValidateIdToken"] = true.ToString(),
                }));
            b.ConfigureTestServices(services =>
            {
                var descriptor = services.Single(d => d.ServiceType == typeof(ITokenExchangeService));
                services.Remove(descriptor);
                services.AddScoped<ITokenExchangeService>(_ => exchange);
            });
        }).CreateClient();
        fake = exchange;
        return client;
    }

    [Fact]
    public async Task ApiKeyMiddleware_ShouldAllowAnonymousRequest_WhenPathIsTokenExchange()
    {
        var client = ClientWithGatesOn(out _);
        var response = await client.PostAsJsonAsync("/Auth/token", new { code = "auth-code", codeVerifier = "verifier", redirectUri = "https://guito.example.com/auth/callback" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GoogleIdTokenMiddleware_ShouldAllowAnonymousRequest_WhenPathIsTokenExchange()
    {
        // Same middleware-exemption contract asserted with the Google gate on:
        // the exchange is unauthenticated, so no Bearer/x-google-idtoken header.
        var client = ClientWithGatesOn(out var fake);
        var response = await client.PostAsJsonAsync("/Auth/token", new { code = "auth-code", codeVerifier = "verifier", redirectUri = "https://guito.example.com/auth/callback" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(fake.Calls); // reached the controller, not just an auth gate
    }

    [Fact]
    public async Task AuthController_ShouldExchangeTokens_WhenCodeAndVerifierArePresent()
    {
        var client = ClientWithGatesOn(out var fake);
        var response = await client.PostAsJsonAsync("/Auth/token", new { code = "auth-code", codeVerifier = "verifier", redirectUri = "https://guito.example.com/auth/callback" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(fake.Calls);
        Assert.Equal("auth-code", call.Code);
        Assert.Equal("verifier", call.CodeVerifier);
        Assert.Equal("https://guito.example.com/auth/callback", call.RedirectUri);
    }

    [Fact]
    public async Task AuthController_ShouldReturnTokenSet_WhenExchangeSucceeds()
    {
        var client = ClientWithGatesOn(out _);
        var response = await client.PostAsJsonAsync("/Auth/token", new { code = "auth-code", codeVerifier = "verifier", redirectUri = "https://guito.example.com/auth/callback" });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"idToken\":\"fake-id-token\"", body);
        Assert.Contains("\"accessToken\":\"fake-access-token\"", body);
        Assert.Contains("\"expiresIn\":3600", body);
    }

    [Fact]
    public async Task AuthController_ShouldReturnBadRequest_WhenCodeIsMissing()
    {
        var client = ClientWithGatesOn(out var fake);
        var response = await client.PostAsJsonAsync("/Auth/token", new { code = "", codeVerifier = "verifier", redirectUri = "https://guito.example.com/auth/callback" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task AuthController_ShouldReturnRfc6749ErrorBody_WhenGoogleRejectsTheCode()
    {
        // guito-ui parses {error, error_description} from the exchange response
        // (guito-ui tokenFailureMessage); the body must carry the verbatim fields.
        var client = ClientWithGatesOn(out var fake);
        fake.Throw = new GoogleTokenExchangeException(400, "invalid_grant", "code was already redeemed");

        var response = await client.PostAsJsonAsync("/Auth/token", new { code = "auth-code", codeVerifier = "verifier", redirectUri = "https://guito.example.com/auth/callback" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"error\":\"invalid_grant\"", body);
        Assert.Contains("\"error_description\":\"code was already redeemed\"", body);
        Assert.DoesNotContain("fake-client-secret", body);
    }
}
