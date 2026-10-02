using GuitoApi.DataTransferObjects.Input;

namespace GuitoApi.Services.Auth
{
    /// <summary>Google token revocation on sign-out (issue #64): the session's access token → Google's revoke endpoint.</summary>
    public interface IRevokeGoogleTokenService
    {
        Task RevokeAsync(LogoutRequest request, CancellationToken cancellationToken = default);
    }
}
