namespace GuitoApi.Services.Auth;

public interface ILogoutUserSessionService
{
    Task LogoutAsync(string? handle, CancellationToken cancellationToken = default);
}
