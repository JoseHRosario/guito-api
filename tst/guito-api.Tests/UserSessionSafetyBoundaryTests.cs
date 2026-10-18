using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static GuitoApi.Tests.UserSessionBoundaryTests;

namespace GuitoApi.Tests;

public class UserSessionSafetyBoundaryTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Staging", true)]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    [InlineData("Staging", false)]
    [InlineData("Production", false)]
    public async Task Preflight_ShouldAllowCredentialsOnlyForExplicitOrigin_WhenBrowserRequestsSession(string environment, bool trusted)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state) { Environment = environment };
        using var client = Client(host);
        using var request = new HttpRequestMessage(HttpMethod.Options, "/Auth/session/refresh");
        request.Headers.Add("Origin", trusted ? UserSessionTestHost.Origin : "https://evil.test");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-guito-session");

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        if (trusted)
        {
            Assert.Equal(UserSessionTestHost.Origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
            Assert.Contains("x-guito-session", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant());
        }
        else Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Empty(state.Forms);
    }

    [Fact]
    public async Task Logout_ShouldKeepExistingSession_WhenUntrustedOriginAttemptsSignout()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Auth/session/logout") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("Origin", "https://evil.test");
        request.Headers.Add("X-Guito-Session", "1");
        request.Headers.Add("Cookie", cookie);

        // Act
        var rejected = await client.SendAsync(request);
        var renewal = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.False(rejected.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, renewal.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.test/auth/callback", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(UserSessionTestHost.Origin + "/wrong", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(UserSessionTestHost.Origin + "/auth/callback?redirect=evil", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(UserSessionTestHost.Origin + "/auth/callback", "short")]
    [InlineData(UserSessionTestHost.Origin + "/auth/callback", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa+")]
    public async Task Establish_ShouldRejectBadRedirectOrPkce_WhenRequestCannotBeBoundSafely(string redirectUri, string verifier)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var response = await Send(client, "token", body: new { code = "fake-code", codeVerifier = verifier, redirectUri });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(state.Forms);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("old-only")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("wrong-length")]
    public async Task Establish_ShouldFailClosedWithoutCookie_WhenCanonicalEncryptionKeyIsMissingOrInvalid(string failure)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        state.KeyPayload = failure switch
        {
            "missing" => "{}",
            "old-only" => JsonSerializer.Serialize(new { HumanSessionEncryptionKey = state.Key }),
            "null" => "{\"UserSessionEncryptionKey\":null}",
            "empty" => "{\"UserSessionEncryptionKey\":\"\"}",
            "wrong-length" => JsonSerializer.Serialize(new { UserSessionEncryptionKey = Convert.ToBase64String(new byte[31]) }),
            _ => "{\"UserSessionEncryptionKey\":\"fake-sensitive-invalid-key\"}"
        };
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var response = await Login(client);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(state.Database.CredentialsWritten);
        Assert.DoesNotContain("fake-sensitive-invalid-key", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("UserSessionEncryptionKey")]
    [InlineData("usersessionencryptionkey")]
    public async Task Refresh_ShouldReadExistingEncryptedCredentials_WhenCanonicalKeyIsDeserialized(string field)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.KeyPayload = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = state.Key });

        // Act
        var response = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("fake-refresh-credential", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("old-only")]
    [InlineData("wrong-key")]
    [InlineData("wrong-length")]
    [InlineData("malformed")]
    [InlineData("empty")]
    [InlineData("null")]
    [InlineData("whitespace")]
    public async Task Refresh_ShouldFailClosedAndPreserveCookie_WhenCanonicalKeyIsMissingOrInvalid(string failure)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.KeyPayload = failure switch
        {
            "missing" => "{}",
            "old-only" => JsonSerializer.Serialize(new { HumanSessionEncryptionKey = state.Key }),
            _ => JsonSerializer.Serialize(new { UserSessionEncryptionKey = failure switch
            {
                "wrong-key" => Convert.ToBase64String(new byte[32]),
                "wrong-length" => Convert.ToBase64String(new byte[31]),
                "malformed" => "fake-sensitive-invalid-key",
                "empty" => "",
                "whitespace" => " " + state.Key,
                _ => (string?)null
            } })
        };

        // Act
        var response = await Send(client, "refresh", cookie);
        state.KeyPayload = null;
        var recovered = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("fake-sensitive-invalid-key", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Theory]
    [InlineData("aud")]
    [InlineData("iss")]
    [InlineData("exp")]
    [InlineData("signature")]
    public async Task Refresh_ShouldPreserveGoodSession_WhenProviderReturnsInvalidRenewedToken(string failure)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.WrongSignature = failure == "signature";
        state.Claims = claims =>
        {
            if (failure == "exp") claims["exp"] = state.Clock.GetUtcNow().ToUnixTimeSeconds();
            else if (failure != "signature") claims[failure] = "wrong";
        };

        // Act
        var failed = await Send(client, "refresh", cookie);
        state.WrongSignature = false;
        state.Claims = null;
        var recovered = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldPreserveSession_WhenGoogleSigningKeysAreTemporarilyUnavailable()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.KeyFailure = true;

        // Act
        var failed = await Send(client, "refresh", cookie);
        state.KeyFailure = false;
        var recovered = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task Establish_ShouldPreserveExistingSessionAndRfcErrorShape_WhenGoogleRejectsCode()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.Status = HttpStatusCode.BadRequest;
        state.Body = "{\"error\":\"invalid_grant\",\"error_description\":\"fake-refresh-credential\"}";

        // Act
        var failed = await Login(client);
        state.Status = HttpStatusCode.OK;
        state.Body = null;
        var oldSession = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        var body = await failed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_grant", body.GetProperty("error").GetString());
        Assert.True(body.TryGetProperty("error_description", out _));
        Assert.DoesNotContain("fake-refresh-credential", await failed.Content.ReadAsStringAsync());
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, oldSession.StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldKeepNewerLease_WhenObsoleteWorkerReceivesInvalidGrant()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.Status = HttpStatusCode.BadRequest;
        state.Body = "{\"error\":\"invalid_grant\"}";
        state.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var obsolete = Send(client, "refresh", cookie);
        await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        state.Clock.Now = state.Clock.Now.AddMinutes(3);
        state.Entered = null;
        state.Status = HttpStatusCode.OK;
        state.Body = null;

        // Act
        var current = await Send(client, "refresh", cookie);
        state.Continue.SetResult();
        var oldResult = await obsolete;
        var next = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, oldResult.StatusCode);
        Assert.False(oldResult.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldRejectIdleExpiryDuringProviderCall_WhenOriginalSessionExpiresBeforeCompletion()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.Clock.Now = state.Clock.Now.AddDays(7).AddSeconds(-1);
        state.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewal = Send(client, "refresh", cookie);
        await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        state.Clock.Now = state.Clock.Now.AddSeconds(1);
        state.Continue.SetResult();
        var response = await renewal;

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("max-age=0", Assert.Single(response.Headers.GetValues("Set-Cookie")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("__Host-guito-session=garbage")]
    [InlineData("__Host-guito-session=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Refresh_ShouldRequireReauthentication_WhenCookieIsMissingMalformedOrUnknown(string? cookie)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var response = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(state.Forms);
    }
}
