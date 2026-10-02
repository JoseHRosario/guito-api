using System.Net;
using System.Net.Http.Json;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Exceptions;
using GuitoApi.Services.Auth;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

/// <summary>
/// HTTP-boundary tests for the Google token revocation on sign-out (issue #64):
/// POST /Auth/logout delegates to the revoke service with the session's access
/// token, is anonymous-unreachable with both auth gates on, and surfaces
/// Google's RFC 6749 error like the token exchange does.
/// </summary>
public class LogoutBoundaryTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public LogoutBoundaryTests(CustomWebApplicationFactory factory) => _factory = factory;

    /// <summary>Client with BOTH auth gates on and the revoke service faked via DI.</summary>
    private HttpClient ClientWithGatesOn(out FakeRevokeGoogleTokenService fake)
    {
        var revoke = new FakeRevokeGoogleTokenService();
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
                var descriptor = services.Single(d => d.ServiceType == typeof(IRevokeGoogleTokenService));
                services.Remove(descriptor);
                services.AddScoped<IRevokeGoogleTokenService>(_ => revoke);
            });
        }).CreateClient();
        fake = revoke;
        return client;
    }

    /// <summary>Client with the revoke service faked via DI; gates off (same shape as ApiBoundaryTests).</summary>
    private HttpClient ClientWithServiceFaked(out FakeRevokeGoogleTokenService fake)
    {
        var revoke = new FakeRevokeGoogleTokenService();
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                var descriptor = services.Single(d => d.ServiceType == typeof(IRevokeGoogleTokenService));
                services.Remove(descriptor);
                services.AddScoped<IRevokeGoogleTokenService>(_ => revoke);
            })).CreateClient();
        fake = revoke;
        return client;
    }

    [Fact]
    public async Task ApiKeyMiddleware_ShouldReturnUnauthorized_WhenLogoutIsCalledWithNoCredentials()
    {
        // Middleware run order (ADR-0003): the agent gate is first — a request with
        // no credentials at all is rejected there, with its body, not the Google gate's.
        var client = ClientWithGatesOn(out var fake);

        var response = await client.PostAsJsonAsync("/Auth/logout", new { accessToken = "fake-access-token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Missing API key", body); // ApiKeyMiddleware produced this 401
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task GoogleIdTokenMiddleware_ShouldReturnUnauthorized_WhenLogoutCarriesNoIdToken()
    {
        // Google credentials present (Bearer) pass the agent gate — the human path —
        // but without the dual header's x-google-idtoken the Google gate rejects.
        var client = ClientWithGatesOn(out var fake);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer fake-id-token");

        var response = await client.PostAsJsonAsync("/Auth/logout", new { accessToken = "fake-access-token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Missing IdentityToken", body); // GoogleIdTokenMiddleware produced this 401
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task AuthController_ShouldRevokeSessionAccessToken_WhenLogoutIsCalled()
    {
        var client = ClientWithServiceFaked(out var fake);

        var response = await client.PostAsJsonAsync("/Auth/logout", new { accessToken = "fake-access-token" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var call = Assert.Single(fake.Calls);
        Assert.Equal("fake-access-token", call);
    }

    [Fact]
    public async Task AuthController_ShouldReturnBadRequest_WhenAccessTokenIsMissing()
    {
        var client = ClientWithServiceFaked(out var fake);

        var response = await client.PostAsJsonAsync("/Auth/logout", new { accessToken = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task AuthController_ShouldReturnRfc6749ErrorBody_WhenGoogleRejectsTheRevocation()
    {
        // Same error contract as the token exchange: the UI-facing body carries
        // Google's verbatim error/error_description (no credentials inside).
        var client = ClientWithServiceFaked(out var fake);
        fake.Throw = new GoogleTokenExchangeException(502, "upstream", "Google is unreachable");

        var response = await client.PostAsJsonAsync("/Auth/logout", new { accessToken = "fake-access-token" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"error\":\"upstream\"", body);
        Assert.Contains("\"error_description\":\"Google is unreachable\"", body);
    }

    [Fact]
    public async Task CorsMiddleware_ShouldAnswerPreflight_WhenBrowserLogsOutCrossOrigin()
    {
        // The browser's fetch to /Auth/logout is cross-origin from the UI; its
        // OPTIONS preflight must come back with an Access-Control-Allow-Origin
        // header or the browser blocks the sign-out call before it starts.
        var client = ClientWithGatesOn(out _);
        var request = new HttpRequestMessage(HttpMethod.Options, "/Auth/logout");
        request.Headers.Add("Origin", "https://dna69cy69n7jb.cloudfront.net");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"),
            "preflight must carry Access-Control-Allow-Origin or the browser blocks the logout");
    }
}
