using GuitoApi.Model;

namespace GuitoApi.Services.Auth;

public interface IRefreshHumanSessionService
{
    Task<HumanSessionIssue> RefreshAsync(string? handle, CancellationToken cancellationToken = default);
}
