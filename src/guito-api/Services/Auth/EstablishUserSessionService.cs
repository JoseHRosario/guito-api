using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;
using Microsoft.Extensions.Options;

namespace GuitoApi.Services.Auth;

public sealed class EstablishUserSessionService(IGoogleSessionProvider google, IUserSessionRepository sessions,
    IUserSessionCredentialProtector protector, IOptions<AppConfigurationOptions> options, TimeProvider clock,
    ILogger<EstablishUserSessionService> logger) : IEstablishUserSessionService
{
    public async Task<UserSessionIssue> EstablishAsync(TokenExchangeRequest request, CancellationToken cancellationToken = default)
    {
        using var operation = new UserSessionOperation(clock, logger, cancellationToken);
        ValidateRequest(request);
        await operation.RunAsync("database_readiness", sessions.EnsureReadyAsync);
        var tokens = await operation.RunAsync("provider_exchange", token => google.ExchangeAsync(request.Code, request.CodeVerifier, request.RedirectUri, token));
        var identity = await operation.RunAsync("provider_validate", token => google.ValidateAsync(tokens.IdToken, cancellationToken: token));
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new UserSessionException(400, "refresh_token_required", "Sign in again with Google consent to enable persistent login.");
        var handle = UserSessionHandle.Create();
        var hash = UserSessionHandle.Hash(handle)!;
        var encrypted = await operation.RunAsync("credential_encrypt", token => protector.EncryptAsync(tokens.RefreshToken, hash, token));
        var expiry = clock.GetUtcNow().AddDays(7);
        await operation.RunAsync("session_create", token => sessions.CreateAsync(new UserSession(hash, identity.Subject, identity.Email, encrypted, expiry), token));
        return new UserSessionIssue(handle, tokens, expiry);
    }

    private void ValidateRequest(TokenExchangeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.CodeVerifier is null || request.CodeVerifier.Length is < 43 or > 128 ||
            !request.CodeVerifier.All(character => char.IsAsciiLetterOrDigit(character) || "-._~".Contains(character)))
            throw new UserSessionException(400, "invalid_request", "A valid code and PKCE verifier are required.");
        if (!options.Value.Cors.AllowedOrigins.Any(origin => request.RedirectUri == $"{origin}/auth/callback"))
            throw new UserSessionException(400, "invalid_request", "The redirect URI is not allowed.");
    }
}
