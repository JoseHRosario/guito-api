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
[Trait(StagingEndpointFixture.CategoryTrait, StagingEndpointFixture.CategoryValue)]
public class StagingAuthContractTests
{
    [Fact]
    public async Task Healthz_ShouldReturnOk_WhenCalledWithoutCredentials()
    {
        // Arrange
        using var client = StagingEndpointFixture.CreateAnonymousClient();

        // Act
        var response = await client.GetAsync("/healthz");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
    public async Task ExpenseEndpoint_ShouldReturnForbiddenFromEdgeAuthorizer_WhenProductionKeyIsSent()
    {
        // Arrange — the PROD agent key against the staging stack must be denied by
        // the edge authorizer (it only knows the staging key), never accepted.
        using var client = StagingEndpointFixture.CreateAgentClient(StagingEndpointFixture.OtherKey);

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
        var client = StagingEndpointFixture.CreateAnonymousClient();
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
        using var client = StagingEndpointFixture.CreateGoogleClient(StagingEndpointFixture.GoogleIdToken);

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
        using var client = StagingEndpointFixture.CreateAnonymousClient();
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

    /// <summary>
    /// Deliberately omits X-Api-Key while presenting the valid staging key as the
    /// raw Authorization value: the edge authorizer accepts it, and the request
    /// reaches the app, whose ApiKeyMiddleware rejects it with 401 "Missing API key"
    /// (verified live) — the in-app half of the deploy contract.
    /// </summary>
    private static HttpClient StagingEndpointClientWithoutXApiKey()
    {
        var client = StagingEndpointFixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", StagingEndpointFixture.AgentKey);
        return client;
    }
}