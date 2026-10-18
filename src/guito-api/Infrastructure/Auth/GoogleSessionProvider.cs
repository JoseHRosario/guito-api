using System.Text.Json;
using GuitoApi.Configuration;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.Reporting;
using GuitoApi.Infrastructure.Secrets;
using GuitoApi.Model;
using GuitoApi.Repositories;
using Microsoft.Extensions.Options;

namespace GuitoApi.Infrastructure.Auth;

public sealed class GoogleSessionProvider(IHttpClientFactory clients, IHumanAuthSecretProvider secrets,
    IOptions<AppConfigurationOptions> options, TimeProvider clock) : IGoogleSessionProvider
{
    public const string HttpClientName = "GoogleSession";
    private readonly Authentication _authentication = options.Value.Authentication;

    public Task<HumanSessionTokens> ExchangeAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken = default) =>
        RequestAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code,
            ["code_verifier"] = verifier, ["redirect_uri"] = redirectUri }, false, cancellationToken);

    public Task<HumanSessionTokens> RefreshAsync(string credential, CancellationToken cancellationToken = default) =>
        RequestAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = credential }, true, cancellationToken);

    private async Task<HumanSessionTokens> RequestAsync(Dictionary<string, string> form, bool refreshing, CancellationToken cancellationToken)
    {
        EnsureConfiguration();
        form["client_id"] = _authentication.GoogleClientId;
        form["client_secret"] = (await secrets.GetAsync(cancellationToken)).ClientSecret;
        if (string.IsNullOrWhiteSpace(form["client_secret"])) throw HumanSessionException.Unavailable();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token") { Content = new FormUrlEncodedContent(form) };
        using var client = clients.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!response.IsSuccessStatusCode) throw ProviderFailure((int)response.StatusCode, body.RootElement, refreshing);
        var tokens = body.RootElement.Deserialize<HumanSessionTokens>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        if (tokens is null || string.IsNullOrWhiteSpace(tokens.IdToken) || string.IsNullOrWhiteSpace(tokens.AccessToken) || tokens.ExpiresIn <= 0)
            throw HumanSessionException.Unavailable();
        return tokens;
    }

    private static HumanSessionException ProviderFailure(int status, JsonElement body, bool refreshing)
    {
        var error = body.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (refreshing && status == 400 && error == "invalid_grant") return HumanSessionException.Expired();
        if (refreshing || status is < 400 or >= 500 || status == 429) return HumanSessionException.Unavailable();
        // Never echo provider text: even an error description can reflect a credential.
        var safeError = error is "invalid_grant" or "invalid_request" or "invalid_client" or "unauthorized_client" or "unsupported_grant_type" ? error : "invalid_request";
        return new HumanSessionException(status, safeError, "Google could not complete sign in. Please try signing in again.");
    }

    public async Task<HumanSessionIdentity> ValidateAsync(string idToken, string? expectedSubject = null, CancellationToken cancellationToken = default)
    {
        EnsureConfiguration();
        var subject = expectedSubject ?? ReadSubject(idToken);
        using var keyTransport = clients.CreateClient("HumanSessionJwks");
        var keys = new HttpJwksClient(keyTransport);
        var result = await new GoogleTokenValidator(keys, _authentication.GoogleClientId, subject, clock.GetUtcNow()).ValidateAsync(idToken, cancellationToken);
        return ValidateIdentity(result, subject, expectedSubject is not null);
    }

    private HumanSessionIdentity ValidateIdentity(GoogleTokenResult result, string subject, bool refreshing)
    {
        if (!result.Valid)
        {
            // A bad provider token is not proof that stored refresh permission was revoked.
            // Only a cryptographically validated identity/policy rejection invalidates that session.
            var identityRejected = result.FailureReason is "subject mismatch" or "email not verified" or "missing email";
            if (refreshing && !identityRejected) throw HumanSessionException.Unavailable();
            throw HumanSessionException.Expired();
        }
        if (result.Claims is null || !_authentication.AllowedLogins.Contains(result.Claims.Email))
            throw HumanSessionException.Expired();
        return new HumanSessionIdentity(subject, result.Claims.Email);
    }

    private void EnsureConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_authentication.GoogleClientId) || _authentication.GoogleClientId != _authentication.OAuthAudience || _authentication.AllowedLogins.Count == 0)
            throw HumanSessionException.Unavailable();
    }

    private static string ReadSubject(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) throw HumanSessionException.Expired();
            using var payload = JsonDocument.Parse(GoogleTokenValidator.Base64UrlDecodeBytes(parts[1]));
            var subject = payload.RootElement.GetProperty("sub").GetString();
            return !string.IsNullOrWhiteSpace(subject) ? subject : throw HumanSessionException.Expired();
        }
        catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        { throw HumanSessionException.Expired(); }
    }
}
