using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GuitoApi.Tests;

public class UserSessionBoundaryTests
{
    [Theory]
    [InlineData("token")]
    [InlineData("refresh")]
    [InlineData("logout")]
    public async Task Session_ShouldRejectMissingOrigin_WhenBrowserMutatesCookie(string operation)
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory();
        using var client = Client(factory);

        // Act
        var response = await client.PostAsJsonAsync($"/Auth/session/{operation}", new { });

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("https://evil.test", "1", "application/json", 403)]
    [InlineData("null", "1", "application/json", 403)]
    [InlineData(UserSessionTestHost.Origin, "", "application/json", 403)]
    [InlineData(UserSessionTestHost.Origin, "2", "application/json", 403)]
    [InlineData(UserSessionTestHost.Origin, "1", "text/plain", 415)]
    [InlineData(UserSessionTestHost.Origin, "1", "application/x-www-form-urlencoded", 415)]
    public async Task Session_ShouldRejectUnsafeRequest_WhenOriginHeaderOrContentTypeIsWrong(string origin, string header, string contentType, int status)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Auth/session/logout") { Content = new StringContent("{}", null, contentType) };
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("X-Guito-Session", header);
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Empty(state.Forms);
    }

    [Fact]
    public async Task Establish_ShouldReturnOnlyShortLivedTokensAndSecureCookie_WhenPkceAndIdentityAreValid()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var response = await Login(client);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertShortLivedTokenPayloadAsync(response);
        AssertSecureSessionCookie(response);
        Assert.True(response.Headers.CacheControl!.NoStore);
        AssertPkceProviderExchange(state);
        Assert.StartsWith("v1.", Assert.Single(state.Database.CredentialsWritten));
    }

    [Fact]
    public async Task Refresh_ShouldSurviveHostRestartAndRetainCredential_WhenGoogleOmitsRefreshToken()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        string cookie;
        using (var first = new UserSessionTestHost(state))
        using (var browser = Client(first)) cookie = Cookie(await Login(browser));
        state.Clock.Now = state.Clock.Now.AddDays(6);
        state.RefreshCredential = null;
        using var restarted = new UserSessionTestHost(state);
        using var client = Client(restarted);

        // Act
        var response = await Send(client, "refresh", cookie);
        state.Clock.Now = state.Clock.Now.AddDays(6);
        var second = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("2030-01-14T12:00:00+00:00", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionExpiresAt").GetString());
        Assert.Equal("fake-refresh-credential", state.Forms[1]["refresh_token"]);
        Assert.Equal("fake-refresh-credential", state.Forms[2]["refresh_token"]);
    }

    [Theory]
    [InlineData(-1, 200)]
    [InlineData(0, 401)]
    [InlineData(1, 401)]
    public async Task Refresh_ShouldEnforceExactIdleBoundary_WhenSevenDaysElapse(int seconds, int expected)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.Clock.Now = state.Clock.Now.AddDays(7).AddSeconds(seconds);

        // Act
        var response = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(expected, (int)response.StatusCode);
        if (expected == 401)
        {
            Assert.Single(state.Forms);
            Assert.Equal("session_expired", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
            Assert.Contains("max-age=0", Assert.Single(response.Headers.GetValues("Set-Cookie")));
        }
    }

    [Fact]
    public async Task Refresh_ShouldHaveNoAbsoluteLifetime_WhenForegroundUseContinues()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));

        // Act / Assert
        for (var visit = 0; visit < 12; visit++)
        {
            state.Clock.Now = state.Clock.Now.AddDays(6);
            Assert.Equal(HttpStatusCode.OK, (await Send(client, "refresh", cookie)).StatusCode);
        }
    }

    [Theory]
    [InlineData(400, "invalid_client")]
    [InlineData(401, "invalid_client")]
    [InlineData(429, "invalid_grant")]
    [InlineData(500, "invalid_grant")]
    [InlineData(503, "temporarily_unavailable")]
    [InlineData(200, "malformed")]
    [InlineData(0, "network")]
    [InlineData(0, "timeout")]
    public async Task Refresh_ShouldPreserveSession_WhenProviderFailureIsNotDefinitive(int status, string error)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.Clock.Now = state.Clock.Now.AddDays(6);
        state.NetworkFailure = error == "network";
        state.TimeoutFailure = error == "timeout";
        state.Status = status == 0 ? HttpStatusCode.OK : (HttpStatusCode)status;
        state.Body = error == "malformed" ? "not-json" : JsonSerializer.Serialize(new { error, error_description = "fake-refresh-credential" });

        // Act
        var failed = await Send(client, "refresh", cookie);
        state.NetworkFailure = false;
        state.TimeoutFailure = false;
        state.Status = HttpStatusCode.OK;
        state.Body = null;
        var recovered = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.Equal("temporarily_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.DoesNotContain("fake-refresh-credential", await failed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldNotExtendIdleExpiry_WhenTemporaryFailureOccurs()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.Clock.Now = state.Clock.Now.AddDays(6);
        state.NetworkFailure = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Send(client, "refresh", cookie)).StatusCode);
        state.Clock.Now = state.Clock.Now.AddDays(1);
        state.NetworkFailure = false;

        // Act
        var response = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldPermanentlyInvalidateOnlySession_WhenGoogleReturnsInvalidGrant()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var first = Cookie(await Login(client));
        var otherDevice = Cookie(await Login(client));
        state.Status = HttpStatusCode.BadRequest;
        state.Body = "{\"error\":\"invalid_grant\",\"error_description\":\"fake-refresh-credential\"}";

        // Act
        var response = await Send(client, "refresh", first);
        state.Status = HttpStatusCode.OK;
        state.Body = null;
        var retry = await Send(client, "refresh", first);
        var other = await Send(client, "refresh", otherDevice);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.DoesNotContain("fake-refresh-credential", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Refresh_ShouldUseRotatedCredential_WhenGoogleProvidesOne()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.RefreshCredential = "fake-rotated-credential";
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "refresh", cookie)).StatusCode);
        state.RefreshCredential = null;

        // Act
        var response = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fake-rotated-credential", state.Forms.Last()["refresh_token"]);
        Assert.All(state.Database.CredentialsWritten, value => Assert.DoesNotContain("fake-rotated-credential", value));
    }

    [Fact]
    public async Task Establish_ShouldFailExplicitlyAndKeepOldSession_WhenRefreshPermissionIsMissing()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var old = Cookie(await Login(client));
        state.RefreshCredential = null;

        // Act
        var response = await Login(client);
        var oldRenewal = await Send(client, "refresh", old);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("refresh_token_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, oldRenewal.StatusCode);
    }

    [Theory]
    [InlineData("aud")]
    [InlineData("iss")]
    [InlineData("email")]
    [InlineData("email_verified")]
    [InlineData("exp")]
    [InlineData("signature")]
    [InlineData("sub")]
    public async Task Establish_ShouldRejectInvalidGoogleIdentity_WhenTokenIsNotAuthoritative(string claim)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        state.Claims = claims => ChangeClaim(claims, claim, state.Clock.GetUtcNow());
        state.WrongSignature = claim == "signature";
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var response = await Login(client);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(state.Database.CredentialsWritten);
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("email")]
    public async Task Refresh_ShouldRejectChangedIdentity_WhenRenewedTokenChangesOwner(string claim)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.Claims = claims => claims[claim] = "someone-else";

        // Act
        var response = await Send(client, "refresh", cookie);
        state.Claims = null;
        var retry = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
    }

    [Fact]
    public async Task Logout_ShouldBeIdempotentAndDeviceLocal_WhenIdTokenIsAbsent()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var first = Cookie(await Login(client));
        var otherDevice = Cookie(await Login(client));

        // Act
        var response = await Send(client, "logout", first);
        var repeated = await Send(client, "logout", first);
        var missing = await Send(client, "logout");
        var firstRefresh = await Send(client, "refresh", first);
        var otherRefresh = await Send(client, "refresh", otherDevice);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, repeated.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, missing.StatusCode);
        Assert.Contains("max-age=0", Assert.Single(response.Headers.GetValues("Set-Cookie")));
        Assert.Equal(HttpStatusCode.Unauthorized, firstRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherRefresh.StatusCode);
        Assert.Equal(3, state.Forms.Count);
        Assert.Equal(0, state.UnexpectedGoogleRequests);
    }

    [Fact]
    public async Task Refresh_ShouldNotResurrectSession_WhenLogoutWinsDuringGoogleRequest()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewal = Send(client, "refresh", cookie);
        await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        var logout = await Send(client, "logout", cookie);
        state.Continue.SetResult();
        var completed = await renewal;
        state.Entered = null;
        var retry = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, completed.StatusCode);
        Assert.False(completed.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldAvoidConcurrentProviderRefresh_WhenAnotherTabHoldsLease()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Send(client, "refresh", cookie);
        await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        var competing = await Send(client, "refresh", cookie);
        state.Continue.SetResult();
        var completed = await first;

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, competing.StatusCode);
        Assert.False(competing.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(2, state.Forms.Count);
    }

    [Theory]
    [InlineData("missing-key")]
    [InlineData("wrong-key")]
    [InlineData("corrupt")]
    [InlineData("database")]
    public async Task Refresh_ShouldPreserveSession_WhenStorageOrKeyIsUnavailable(string failure)
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        var original = state.Key;
        state.Key = failure == "missing-key" ? "" : failure == "wrong-key" ? Convert.ToBase64String(new byte[32]) : original;
        state.Database.CorruptCredential = failure == "corrupt";
        state.Database.Fail = failure == "database";

        // Act
        var failed = await Send(client, "refresh", cookie);
        state.Key = original;
        state.Database.CorruptCredential = false;
        state.Database.Fail = false;
        var recovered = await Send(client, "refresh", cookie);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("fake-refresh-credential", await failed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task Business_ShouldRejectCookieOnlyAuthentication_WhenSessionExists()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Expense/latest/5");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Missing API key", await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertShortLivedTokenPayloadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, json.EnumerateObject().Count());
        Assert.Equal(3600, json.GetProperty("expiresIn").GetInt32());
        Assert.Equal("2030-01-08T12:00:00+00:00", json.GetProperty("sessionExpiresAt").GetString());
        Assert.DoesNotContain("fake-refresh-credential", await response.Content.ReadAsStringAsync());
    }

    private static void AssertSecureSessionCookie(HttpResponseMessage response)
    {
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{UserSessionController.CookieName}=", cookie);
        Assert.Contains("max-age=604800", cookie);
        Assert.Contains("path=/", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.DoesNotContain("domain=", cookie);
    }

    private static void AssertPkceProviderExchange(UserSessionTestHost.State state)
    {
        var form = Assert.Single(state.Forms);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(new string('a', 43), form["code_verifier"]);
        Assert.Equal(UserSessionTestHost.Origin + "/auth/callback", form["redirect_uri"]);
        Assert.Equal(UserSessionTestHost.ClientId, form["client_id"]);
        Assert.Equal("fake-client-secret", form["client_secret"]);
    }

    private static void ChangeClaim(Dictionary<string, object> claims, string claim, DateTimeOffset now)
    {
        if (claim == "signature") return;
        claims[claim] = claim switch { "email_verified" => false, "exp" => now.ToUnixTimeSeconds(), "sub" => "", _ => "wrong" };
    }

    internal static HttpClient Client(CustomWebApplicationFactory host) => host.CreateClient(new WebApplicationFactoryClientOptions
    { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
    internal static Task<HttpResponseMessage> Login(HttpClient client) => Send(client, "token", body: new
    { code = "fake-code", codeVerifier = new string('a', 43), redirectUri = UserSessionTestHost.Origin + "/auth/callback" });
    internal static string Cookie(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
    }
    internal static Task<HttpResponseMessage> Send(HttpClient client, string operation, string? cookie = null, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/Auth/session/{operation}") { Content = JsonContent.Create(body ?? new { }) };
        request.Headers.Add("Origin", UserSessionTestHost.Origin);
        request.Headers.Add("X-Guito-Session", "1");
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return client.SendAsync(request);
    }
}
