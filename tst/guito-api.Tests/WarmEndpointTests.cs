using System.Net;

namespace GuitoApi.Tests;

/// <summary>
/// Contract of the public warm endpoint (issue #109): an anonymous GET /warm
/// warms the app container and issues one cheap Postgres query (the Aurora
/// min-0-ACU resume wake), capped server-side — a returned-then-background
/// task is not guaranteed to run in Lambda, so the ping is awaited (bounded).
/// Best effort: a failed ping still warms the container, so /warm returns 200.
/// The public-route exemption must be granted in BOTH middlewares (path-aware
/// auth defense, ADR-0003) — an anonymous 200 here proves both pass /warm.
/// </summary>
public class WarmEndpointTests
{
    [Fact]
    public async Task Warm_ShouldReturnOkAnonymously_WhenCalledWithoutCredentials()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/warm");

        // Assert — a 401 here means the middleware exemption is missing;
        // a gateway-shaped rejection means the route exemption is missing.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Warm_ShouldPingPostgresWithSelectOne_WhenCalled()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        // Act
        await client.GetAsync("/warm");

        // Assert — the wake is a cheap statement the resume-retry client already
        // knows how to survive (DatabaseResumingException backoff, #91).
        Assert.Contains("SELECT 1", factory.Postgres.ExecutedSql);
    }

    [Fact]
    public async Task Warm_ShouldPassABoundedCancellationToken_WhenPingingPostgres()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        // Act
        await client.GetAsync("/warm");

        // Assert — the cap rides the cancellation token: a token that can never
        // fire means the invocation could hang on a stuck DB wake.
        var token = Assert.Single(factory.Postgres.ExecutedCancellationTokens);
        Assert.True(token.CanBeCanceled, "/warm must bound its DB ping with a cancellable token (~10s cap)");
    }

    [Fact]
    public async Task Warm_ShouldReturnOk_WhenPostgresPingFails()
    {
        // Arrange — best effort by design: the container is warmed either way;
        // a broken datastore must not turn the warm endpoint into an alarm.
        using var factory = new CustomWebApplicationFactory();
        factory.Postgres.ExecuteException = new InvalidOperationException("db down");
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/warm");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
