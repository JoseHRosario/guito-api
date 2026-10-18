using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;
using Microsoft.Extensions.Options;

namespace GuitoApi.Services.Auth;

public sealed class EstablishHumanSessionService(IGoogleSessionProvider google, IHumanSessionRepository sessions,
    IHumanSessionCredentialProtector protector, IOptions<AppConfigurationOptions> options, TimeProvider clock) : IEstablishHumanSessionService
{
    public async Task<HumanSessionIssue> EstablishAsync(TokenExchangeRequest request, CancellationToken cancellationToken = default)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(25));
        cancellationToken = bounded.Token;
        ValidateRequest(request);
        var tokens = await google.ExchangeAsync(request.Code, request.CodeVerifier, request.RedirectUri, cancellationToken);
        var identity = await google.ValidateAsync(tokens.IdToken, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new HumanSessionException(400, "refresh_token_required", "Sign in again with Google consent to enable persistent login.");
        var handle = HumanSessionHandle.Create();
        var hash = HumanSessionHandle.Hash(handle)!;
        var encrypted = await protector.EncryptAsync(tokens.RefreshToken, hash, cancellationToken);
        var expiry = clock.GetUtcNow().AddDays(7);
        await sessions.CreateAsync(new HumanSession(hash, identity.Subject, identity.Email, encrypted, expiry), cancellationToken);
        return new HumanSessionIssue(handle, tokens, expiry);
    }

    private void ValidateRequest(TokenExchangeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.CodeVerifier is null || request.CodeVerifier.Length is < 43 or > 128 ||
            !request.CodeVerifier.All(character => char.IsAsciiLetterOrDigit(character) || "-._~".Contains(character)))
            throw new HumanSessionException(400, "invalid_request", "A valid code and PKCE verifier are required.");
        if (!options.Value.Cors.AllowedOrigins.Any(origin => request.RedirectUri == $"{origin}/auth/callback"))
            throw new HumanSessionException(400, "invalid_request", "The redirect URI is not allowed.");
    }
}
