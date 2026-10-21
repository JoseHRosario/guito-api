using GuitoApi.Model;

namespace GuitoApi.Services.Auth;

public interface IRefreshUserSessionService
{
    Task<UserSessionIssue> RefreshAsync(string? handle, CancellationToken cancellationToken = default);
}
