using GuitoApi.Exceptions;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.DataTransferObjects.Input;
using System.Net;
using GuitoApi.Configuration;
using GuitoApi.Services;
using GuitoApi.Services.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

/// <summary>
/// Service-level tests for the Google token exchange (issue #52): the request
/// to Google carries client_id + client_secret + PKCE fields, the token set is
/// returned, and Google's RFC 6749 error surfaces with the right status.
/// Google is faked at the HTTP-transport level (FakeGoogleTokenHttpHandler).
/// </summary>
public class GoogleTokenExchangeServiceTests
{
    private readonly FakeGoogleTokenHttpHandler _http = new();
    private readonly FakeHumanAuthSecretProvider _secrets = new();

    private GoogleTokenExchangeService CreateService()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(GoogleTokenExchangeService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => _http);
        var httpClientFactory = services.BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>();
        var options = Microsoft.Extensions.Options.Options.Create(new AppConfigurationOptions
        {
            Authentication = new Authentication { GoogleClientId = "test-client-id" },
        });
        return new GoogleTokenExchangeService(httpClientFactory, options, _secrets);
    }

    private static TokenExchangeRequest ValidRequest() => new()
    {
        Code = "auth-code",
        CodeVerifier = "pkce-verifier",
        RedirectUri = "https://guito.example.com/auth/callback",
    };

    [Fact]
    public async Task ExchangeAsync_ShouldPostClientSecretAndPkceFields_WhenExchanging()
    {
        var service = CreateService();

        await service.ExchangeAsync(ValidRequest());

        var body = _http.RequestBodies.Last();
        var form = System.Web.HttpUtility.ParseQueryString(body);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("auth-code", form["code"]);
        Assert.Equal("pkce-verifier", form["code_verifier"]);
        Assert.Equal("https://guito.example.com/auth/callback", form["redirect_uri"]);
        Assert.Equal("test-client-id", form["client_id"]);
        Assert.Equal("fake-client-secret", form["client_secret"]);
        // The secret must never travel anywhere but the form body.
        Assert.DoesNotContain("fake-client-secret", _http.RequestUrls.Last());
    }

    [Fact]
    public async Task ExchangeAsync_ShouldReturnTokenSet_WhenGoogleSucceeds()
    {
        var service = CreateService();

        var result = await service.ExchangeAsync(ValidRequest());

        Assert.Equal("fake-id-token", result.IdToken);
        Assert.Equal("fake-access-token", result.AccessToken);
        Assert.Equal(3599, result.ExpiresIn);
    }

    [Fact]
    public async Task ExchangeAsync_ShouldThrowRfc6749Error_WhenGoogleRejectsTheCode()
    {
        // Status convention: use the upstream status when the response carries one —
        // Google 400 (invalid_grant etc.) surfaces as 400 with the provider error.
        _http.NextStatus = HttpStatusCode.BadRequest;
        _http.NextBody = """{"error":"invalid_grant","error_description":"code was already redeemed"}""";
        var service = CreateService();

        var exception = await Assert.ThrowsAnyAsync<GoogleTokenExchangeException>(() => service.ExchangeAsync(ValidRequest()));

        Assert.Equal(400, exception.HttpStatusCode);
        Assert.Equal("invalid_grant", exception.Error);
        Assert.Equal("code was already redeemed", exception.ErrorDescription);
    }

    [Fact]
    public async Task ExchangeAsync_ShouldReturnBadGateway_WhenGoogleFailsServerError()
    {
        // Status convention: upstream provider failure with a 5xx → 502.
        _http.NextStatus = HttpStatusCode.InternalServerError;
        _http.NextBody = "server error";
        var service = CreateService();

        var exception = await Assert.ThrowsAnyAsync<GoogleTokenExchangeException>(() => service.ExchangeAsync(ValidRequest()));

        Assert.Equal(502, exception.HttpStatusCode);
    }

    [Fact]
    public async Task ExchangeAsync_ShouldReturnBadRequest_WhenCodeVerifierIsMissing()
    {
        var service = CreateService();

        var request = ValidRequest();
        request.CodeVerifier = "";
        var exception = await Assert.ThrowsAnyAsync<ProblemException>(() => service.ExchangeAsync(request));

        Assert.Equal(400, exception.HttpStatusCode);
        Assert.Empty(_http.RequestUrls); // never reached Google
    }
}
