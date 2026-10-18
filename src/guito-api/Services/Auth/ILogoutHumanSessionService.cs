namespace GuitoApi.Services.Auth;

public interface ILogoutHumanSessionService
{
    Task LogoutAsync(string? handle, CancellationToken cancellationToken = default);
}
