using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Auth;

public sealed class RefreshHumanSessionService(IGoogleSessionProvider google, IHumanSessionRepository sessions,
    IHumanSessionCredentialProtector protector, TimeProvider clock) : IRefreshHumanSessionService
{
    public async Task<HumanSessionIssue> RefreshAsync(string? handle, CancellationToken cancellationToken = default)
    {
        var hash = HumanSessionHandle.Hash(handle) ?? throw HumanSessionException.Expired();
        var lease = HumanSessionHandle.Create();
        var session = await sessions.AcquireAsync(hash, lease, clock.GetUtcNow(), cancellationToken) ?? throw HumanSessionException.Expired();
        try { return await RenewAsync(handle!, session, lease, cancellationToken); }
        catch (HumanSessionException exception) when (exception.StatusCode == 401)
        {
            if (!await sessions.InvalidateAsync(hash, lease, cancellationToken)) throw HumanSessionException.Unavailable();
            throw;
        }
        finally
        {
            // Cleanup must finish before Lambda freezes the request. A crashed worker's lease expires in two minutes.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await sessions.ReleaseAsync(hash, lease, cleanup.Token); }
            catch (Exception) { /* The bounded lease, not a background task, recovers provider/DB outages. */ }
        }
    }

    private async Task<HumanSessionIssue> RenewAsync(string handle, HumanSession session, string lease, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(20));
        var credential = await protector.DecryptAsync(session.EncryptedCredential, session.HandleHash, bounded.Token);
        var tokens = await google.RefreshAsync(credential, bounded.Token);
        var identity = await google.ValidateAsync(tokens.IdToken, session.Subject, bounded.Token);
        if (identity.Email != session.Email) throw HumanSessionException.Expired();
        var encrypted = string.IsNullOrWhiteSpace(tokens.RefreshToken) ? session.EncryptedCredential
            : await protector.EncryptAsync(tokens.RefreshToken, session.HandleHash, bounded.Token);
        var now = clock.GetUtcNow();
        if (session.ExpiresAt <= now) throw HumanSessionException.Expired();
        var expiry = now.AddDays(7);
        if (!await sessions.CompleteAsync(session.HandleHash, lease, encrypted, now, expiry, bounded.Token))
            throw HumanSessionException.Unavailable();
        return new HumanSessionIssue(handle, tokens, expiry);
    }
}
