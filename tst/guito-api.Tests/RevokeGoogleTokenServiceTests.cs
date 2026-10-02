using System.Net;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Exceptions;
using GuitoApi.Services.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

/// <summary>
/// Service-level tests for the Google token revocation on sign-out (issue #64):
/// the request to Google carries the session's access token at the revoke
/// endpoint, Google's success and idempotent already-revoked results are 2xx
/// success, and Google's RFC 6749 error surfaces with the right status.
/// Google is faked at the HTTP-transport level (FakeGoogleTokenHttpHandler).
/// </summary>
public class RevokeGoogleTokenServiceTests
{
    private readonly FakeGoogleTokenHttpHandler _http = new();

    private RevokeGoogleTokenService CreateService()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(RevokeGoogleTokenService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => _http);
        var httpClientFactory = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>();
        return new RevokeGoogleTokenService(httpClientFactory);
    }

    private static LogoutRequest ValidRequest() => new()
    {
        AccessToken = "fake-access-token",
    };

    [Fact]
    public async Task RevokeAsync_ShouldPostAccessTokenAtGoogleRevokeEndpoint_WhenRevoking()
    {
        // Arrange
        var service = CreateService();

        // Act
        await service.RevokeAsync(ValidRequest());

        // Assert
        Assert.Equal(RevokeGoogleTokenService.RevokeEndpoint, _http.RequestUrls.Single());
        var form = System.Web.HttpUtility.ParseQueryString(_http.RequestBodies.Single());
        Assert.Equal("fake-access-token", form["token"]);
        // The token is a bearer credential: it must never travel in the URL.
        Assert.DoesNotContain("fake-access-token", _http.RequestUrls.Single());
    }

    [Fact]
    public async Task RevokeAsync_ShouldReturnSuccess_WhenGoogleAcceptsTheRevocation()
    {
        // Arrange
        _http.NextStatus = HttpStatusCode.OK;
        var service = CreateService();

        // Act
        await service.RevokeAsync(ValidRequest());
    }

    [Fact]
    public async Task RevokeAsync_ShouldReturnIdempotentSuccess_WhenTokenIsAlreadyRevoked()
    {
        // Arrange
        // Google revocation is idempotent (documented semantics): invalid_token
        // means the token already expired or was already revoked — the sign-out
        // goal is achieved either way, so this surfaces as success, not error.
        _http.NextStatus = HttpStatusCode.BadRequest;
        _http.NextBody = """{"error":"invalid_token","error_description":"Token expired or revoked."}""";
        var service = CreateService();

        // Act
        await service.RevokeAsync(ValidRequest());
    }

    [Fact]
    public async Task RevokeAsync_ShouldThrowRfc6749Error_WhenGoogleRejectsTheToken()
    {
        // Arrange
        // Status convention: use the upstream status when the response carries one —
        // Google 4xx (other than the idempotent invalid_token) surfaces as-is.
        _http.NextStatus = HttpStatusCode.BadRequest;
        _http.NextBody = """{"error":"invalid_request","error_description":"Token is not revocable."}""";
        var service = CreateService();

        // Act
        var exception = await Assert.ThrowsAnyAsync<GoogleTokenExchangeException>(() => service.RevokeAsync(ValidRequest()));

        // Assert
        Assert.Equal(400, exception.HttpStatusCode);
        Assert.Equal("invalid_request", exception.Error);
        Assert.Equal("Token is not revocable.", exception.ErrorDescription);
    }

    [Fact]
    public async Task RevokeAsync_ShouldThrowBadGateway_WhenGoogleFailsServerError()
    {
        // Arrange
        // Status convention: upstream provider failure with a 5xx → 502.
        _http.NextStatus = HttpStatusCode.InternalServerError;
        _http.NextBody = "server error";
        var service = CreateService();

        // Act
        var exception = await Assert.ThrowsAnyAsync<GoogleTokenExchangeException>(() => service.RevokeAsync(ValidRequest()));

        // Assert
        Assert.Equal(502, exception.HttpStatusCode);
    }

    [Fact]
    public async Task RevokeAsync_ShouldReturnBadRequest_WhenAccessTokenIsMissing()
    {
        // Arrange
        var service = CreateService();

        // Act
        var exception = await Assert.ThrowsAnyAsync<ProblemException>(() => service.RevokeAsync(new LogoutRequest { AccessToken = "" }));

        // Assert
        Assert.Equal(400, exception.HttpStatusCode);
        Assert.Empty(_http.RequestUrls); // never reached Google
    }
}
