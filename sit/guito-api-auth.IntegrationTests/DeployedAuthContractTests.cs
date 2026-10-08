using System.Net;
using GuitoApi.IntegrationTests.Common;

namespace GuitoApi.IntegrationTests.Auth;

/// <summary>
/// Auth contract of the DEPLOYED staging stack (issue #22), with layer attribution
/// from the response bodies — each rejection names the layer that produced it:
///   • 401 {"message":"Unauthorized"} → the gateway rejected the request before
///     any authorizer ran: its single identity source (the Authorization header)
///     was absent;
///   • 401 "Missing API key"          → the edge passed the request, but the in-app
///     ApiKeyMiddleware saw no X-Api-Key;
///   • 403 {"message":"Forbidden"}    → the edge authorizer ran and denied the key
///     (a custom-authorizer Deny surfaces as the gateway's Forbidden body);
///   • 403 "Invalid API key"          → the edge passed, the in-app middleware
///     rejected the key — a different failure from an edge deny.
/// </summary>
[Trait(DeployedEndpointFixture.CategoryTrait, DeployedEndpointFixture.CategoryValue)]
public class DeployedAuthContractTests
{
    [Fact]
    public async Task Healthz_ShouldReturnOk_WhenCalledWithoutCredentials()
    {
        // Arrange
        using var client = DeployedEndpointFixture.CreateAnonymousClient();

        // Act
        var response = await client.GetAsync("/healthz");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TokenExchangeEndpoint_ShouldReachTheApp_WhenCalledAnonymously()
    {
        // Arrange — the exchange is unauthenticated by design (issue #52): the code
        // is the credential. An empty body is rejected by [Required], so the app
        // answers with its own 400; a gateway 401/403 here would instead mean the
        // public route (ANY /Auth/token) is missing from the deployed API.
        using var client = DeployedEndpointFixture.CreateAnonymousClient();

        // Act
        var response = await client.PostAsync("/Auth/token",
            new StringContent("""{"code":"","codeVerifier":"x","redirectUri":"x"}""", System.Text.Encoding.UTF8, "application/json"));

        // Assert — must NOT be a gateway 401/403 (route exemption) and must NOT be
        // 500 from the authorizer cold start; 400 is the app's [Required] verdict.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExpenseEndpoint_ShouldReturnUnauthorizedFromGateway_WhenNoCredentialsArePresent()
    {
        // Arrange
        using var client = StagingEndpointClientWithoutAuthorizationHeader();

        // Act
        var response = await client.GetAsync("/Expense/latest/5");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Unauthorized", await response.Content.ReadAsStringAsync());
        // Gateway-generated body (no validator ran); the in-app middlewares produce different bodies.
    }

    [Fact]
    public async Task ExpenseEndpoint_ShouldReturnUnauthorizedFromAppMiddleware_WhenXApiKeyIsMissing()
    {
        // Arrange — the valid staging key in Authorization alone: the edge accepts,
        // the request reaches the app, and ApiKeyMiddleware rejects the missing
        // X-Api-Key with its own 401 body.
        using var client = StagingEndpointClientWithoutXApiKey();

        // Act
        var response = await client.GetAsync("/Expense/latest/5");

        // Assert — 401 {"message":"Unauthorized"} here would mean the gateway never
        // forwarded the request (identity-source contract broken), not a middleware
        // verdict; assert the middleware's body explicitly.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Missing API key", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExpenseEndpoint_ShouldReturnForbiddenFromEdgeAuthorizer_WhenOtherEnvironmentKeyIsSent()
    {
        // Arrange — the OTHER environment's agent key against the target stack
        // must be denied by the edge authorizer (it only knows the target's
        // key), never accepted — in either direction (staging↔prod).
        using var client = DeployedEndpointFixture.CreateAgentClient(DeployedEndpointFixture.OtherKey);

        // Act
        var response = await client.GetAsync("/Expense/latest/5");

        // Assert — a custom-authorizer Deny reaches the client as the gateway's
        // 403 {"message":"Forbidden"} (verified against the authorizer's live log
        // "Agent key rejected: unknown X-Api-Key"). 403 with the in-app middleware's
        // "Invalid API key" body would instead mean the edge let a foreign key
        // through and the app denied it — a different failure.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Forbidden", body);
        Assert.DoesNotContain("Invalid API key", body);
    }

    /// <summary>
    /// Deliberately omits the raw Authorization value — API Gateway's only identity
    /// source — while sending an (irrelevant) X-Api-Key, so the rejection happens at
    /// the gateway itself, before any authorizer or middleware. Distinct from a
    /// key-validation failure and from the in-app missing-key case below.
    /// </summary>
    private static HttpClient StagingEndpointClientWithoutAuthorizationHeader()
    {
        var client = DeployedEndpointFixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "irrelevant-missing-authorization-header");
        return client;
    }

    /// <summary>
    /// Human path, positive case (ADR-0003): a REAL Google ID token minted by the
    /// runner, carried in BOTH headers — Authorization "Bearer &lt;token&gt;" (gateway
    /// identity source → edge GoogleTokenValidator Allow) and x-google-idtoken
    /// (in-app GoogleIdTokenMiddleware). Proves the human path end-to-end: the edge
    /// authorizer validates against Google's live JWKS and the app accepts its own
    /// middleware credential.
    /// </summary>
    [Fact]
    public async Task ExpenseEndpoint_ShouldReturnOk_WhenValidGoogleIdTokenIsPresentInBothHeaders()
    {
        // Arrange
        using var client = DeployedEndpointFixture.CreateGoogleClient(DeployedEndpointFixture.GoogleIdToken);

        // Act
        var response = await client.GetAsync("/Expense/latest/5");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Human path, negative case: a garbage Bearer value dispatches to the EDGE
    /// Google validator (Authorization starts with "Bearer "), which denies it —
    /// surfacing as the gateway's 403 {"message":"Forbidden"}. If the body instead
    /// named the agent middlewares ("Missing API key"/"Invalid API key"), the
    /// dispatcher would have routed a Bearer request into the agent path — a broken
    /// identity-source contract, not a token-validation failure.
    /// </summary>
    [Fact]
    public async Task ExpenseEndpoint_ShouldReturnForbiddenFromEdgeAuthorizer_WhenBearerValueIsNotAValidGoogleToken()
    {
        // Arrange
        using var client = DeployedEndpointFixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer not-a-real-google-id-token");

        // Act
        var response = await client.GetAsync("/Expense/latest/5");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Forbidden", body);
        Assert.DoesNotContain("Invalid API key", body);
        Assert.DoesNotContain("Missing API key", body);
    }

    /// <summary>Shared happy-path body for POST /Auth/logout: a garbage token (never a real one) must come back 204.</summary>
    private static async Task<HttpStatusCode> LogoutGarbageTokenStatus(HttpClient client)
    {
        var response = await client.PostAsync("/Auth/logout",
            DeployedEndpointFixture.ToJsonContent(new { accessToken = $"guito-sit-garbage-{Guid.NewGuid():N}" }));
        return response.StatusCode;
    }

    /// <summary>
    /// POST /Auth/logout (issue #64), agent path: the TARGET agent key in both
    /// headers gates the endpoint (it is NOT public like /Auth/token — a 401 here
    /// means the auth contract broke, never the route). The body revokes a GARBAGE
    /// token: Google's live revoke endpoint answers 400 invalid_token ("already
    /// expired or already revoked"), which the endpoint's idempotent contract
    /// surfaces as 204 — proving the real revocation round-trip end-to-end while
    /// harming no real token (NEVER revoke a runner-minted token: that grant's
    /// refresh token is the human-auth minting source itself).
    /// </summary>
    [Fact]
    public async Task LogoutEndpoint_ShouldReturnNoContent_WhenAgentRevokesAGarbageToken()
    {
        // Arrange
        using var client = DeployedEndpointFixture.CreateAgentClient(DeployedEndpointFixture.AgentKey);

        // Act + Assert
        Assert.Equal(HttpStatusCode.NoContent, await LogoutGarbageTokenStatus(client));
    }

    /// <summary>
    /// POST /Auth/logout (issue #64), human path: the REAL Google ID token minted by
    /// the runner, carried in BOTH headers — the edge Google validator Allows and the
    /// in-app GoogleIdTokenMiddleware passes the logout through. The body revokes the
    /// same GARBAGE-token case as the agent-path test (never the presented token).
    /// </summary>
    [Fact]
    public async Task LogoutEndpoint_ShouldReturnNoContent_WhenValidGoogleIdTokenIsPresentInBothHeaders()
    {
        // Arrange
        using var client = DeployedEndpointFixture.CreateGoogleClient(DeployedEndpointFixture.GoogleIdToken);

        // Act + Assert
        Assert.Equal(HttpStatusCode.NoContent, await LogoutGarbageTokenStatus(client));
    }

    /// <summary>
    /// POST /Auth/logout, negative case with layer attribution: no Authorization
    /// header → the gateway rejects before any authorizer runs (its 401 body), with
    /// an irrelevant X-Api-Key present so an in-app verdict here would mean the
    /// request was never gated at the edge.
    /// </summary>
    [Fact]
    public async Task LogoutEndpoint_ShouldReturnUnauthorizedFromGateway_WhenNoAuthorizationHeaderIsPresent()
    {
        // Arrange
        var client = DeployedEndpointFixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "irrelevant-missing-authorization-header");

        // Act
        var response = await client.PostAsync("/Auth/logout",
            DeployedEndpointFixture.ToJsonContent(new { accessToken = "guito-sit-garbage" }));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Unauthorized", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// POST /Auth/logout browser preflight (issue #64): the UI's sign-out fetch is
    /// cross-origin, so its OPTIONS must come back 2xx with an Access-Control-Allow-Origin
    /// header (edge CORS + the public OPTIONS route's in-app answer) — a 401/403 here
    /// blocks every real browser sign-out, and HttpClient-only tests can never
    /// surface it.
    /// </summary>
    [Fact]
    public async Task LogoutEndpoint_ShouldAnswerCorsPreflight_WhenBrowserSendsOne()
    {
        // Arrange
        using var client = DeployedEndpointFixture.CreateAnonymousClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/Auth/logout");
        request.Headers.Add("Origin", "https://guito.web.kerumirembora.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.True((int)response.StatusCode >= 200 && (int)response.StatusCode < 300,
            $"preflight must be 2xx, got {(int)response.StatusCode}");
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"),
            "preflight must carry Access-Control-Allow-Origin or the browser blocks the sign-out");
    }

    [Fact]
    public async Task WarmEndpoint_ShouldReturnOk_WhenCalledWithoutCredentials()
    {
        // Arrange — /warm is public by design (issue #109): anonymous 200 proves
        // the route exemption at BOTH layers (gateway route + in-app middlewares).
        using var client = DeployedEndpointFixture.CreateAnonymousClient();

        // Act
        var response = await client.GetAsync("/warm");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WarmEndpoint_ShouldAnswerCorsPreflight_WhenBrowserSendsOne()
    {
        // Arrange — the UI's fire-and-forget warm-up fetch is cross-origin
        // (issue #59): a 401/403 preflight silently kills the wake on every
        // app open, and HttpClient-only tests can never surface it.
        using var client = DeployedEndpointFixture.CreateAnonymousClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/warm");
        request.Headers.Add("Origin", "https://guito.web.kerumirembora.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.True((int)response.StatusCode >= 200 && (int)response.StatusCode < 300,
            $"preflight must be 2xx, got {(int)response.StatusCode}");
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"),
            "preflight must carry Access-Control-Allow-Origin or the browser blocks the warm-up");
    }

    /// <summary>
    /// Deliberately omits X-Api-Key while presenting the valid staging key as the
    /// raw Authorization value: the edge authorizer accepts it, and the request
    /// reaches the app, whose ApiKeyMiddleware rejects it with 401 "Missing API key"
    /// (verified live) — the in-app half of the deploy contract.
    /// </summary>
    private static HttpClient StagingEndpointClientWithoutXApiKey()
    {
        var client = DeployedEndpointFixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", DeployedEndpointFixture.AgentKey);
        return client;
    }
}