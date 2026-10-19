using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Auth;

public sealed class RefreshUserSessionService(IGoogleSessionProvider google, IUserSessionRepository sessions,
    IUserSessionCredentialProtector protector, TimeProvider clock, ILogger<RefreshUserSessionService> logger) : IRefreshUserSessionService
{
    public async Task<UserSessionIssue> RefreshAsync(string? handle, CancellationToken cancellationToken = default)
    {
        var hash = UserSessionHandle.Hash(handle) ?? throw UserSessionException.Expired();
        using var operation = new UserSessionOperation(clock, logger, cancellationToken);
        await operation.RunAsync("database_readiness", sessions.EnsureReadyAsync);
        var lease = UserSessionHandle.Create();
        try { return await AcquireAndRenewAsync(handle!, hash, lease, operation); }
        finally { await ReleaseAsync(hash, lease, operation); }
    }

    private async Task<UserSessionIssue> AcquireAndRenewAsync(string handle, string hash, string lease, UserSessionOperation operation)
    {
        var session = await operation.RunAsync("lease_acquire", token => sessions.AcquireAsync(hash, lease, clock.GetUtcNow(), token))
            ?? throw UserSessionException.Expired();
        try { return await RenewAsync(handle, session, lease, operation); }
        catch (UserSessionException exception) when (exception.StatusCode == 401)
        {
            if (!await operation.RunAsync("session_invalidate", token => sessions.InvalidateAsync(hash, lease, token)))
                throw UserSessionException.Unavailable();
            throw;
        }
    }

    private async Task ReleaseAsync(string hash, string lease, UserSessionOperation operation)
    {
        // Independent of caller cancellation, awaited before Lambda returns; crashed workers retain the durable two-minute lease.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2), clock);
        try { await sessions.ReleaseAsync(hash, lease, cleanup.Token); }
        catch (Exception exception) { operation.LogFailure("lease_release", exception); }
    }

    private async Task<UserSessionIssue> RenewAsync(string handle, UserSession session, string lease, UserSessionOperation operation)
    {
        var credential = await operation.RunAsync("credential_decrypt", token => protector.DecryptAsync(session.EncryptedCredential, session.HandleHash, token));
        var tokens = await operation.RunAsync("provider_refresh", token => google.RefreshAsync(credential, token));
        var identity = await operation.RunAsync("provider_validate", token => google.ValidateAsync(tokens.IdToken, session.Subject, token));
        if (identity.Email != session.Email) throw UserSessionException.Expired();
        var encrypted = string.IsNullOrWhiteSpace(tokens.RefreshToken) ? session.EncryptedCredential
            : await operation.RunAsync("credential_encrypt", token => protector.EncryptAsync(tokens.RefreshToken, session.HandleHash, token));
        var now = clock.GetUtcNow();
        if (session.ExpiresAt <= now) throw UserSessionException.Expired();
        var expiry = now.AddDays(7);
        if (!await operation.RunAsync("session_complete", token => sessions.CompleteAsync(session.HandleHash, lease, encrypted, now, expiry, token)))
            throw UserSessionException.Unavailable();
        return new UserSessionIssue(handle, tokens, expiry);
    }
}
