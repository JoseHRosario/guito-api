using GuitoApi.Repositories;

namespace GuitoApi.Services.Auth;

public sealed class LogoutHumanSessionService(IHumanSessionRepository sessions) : ILogoutHumanSessionService
{
    public async Task LogoutAsync(string? handle, CancellationToken cancellationToken = default)
    {
        var hash = HumanSessionHandle.Hash(handle);
        if (hash is not null) await sessions.DeleteAsync(hash, cancellationToken);
    }
}
