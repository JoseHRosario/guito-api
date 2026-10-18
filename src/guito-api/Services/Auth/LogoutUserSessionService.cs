using GuitoApi.Repositories;

namespace GuitoApi.Services.Auth;

public sealed class LogoutUserSessionService(IUserSessionRepository sessions) : ILogoutUserSessionService
{
    public async Task LogoutAsync(string? handle, CancellationToken cancellationToken = default)
    {
        var hash = UserSessionHandle.Hash(handle);
        if (hash is not null) await sessions.DeleteAsync(hash, cancellationToken);
    }
}
