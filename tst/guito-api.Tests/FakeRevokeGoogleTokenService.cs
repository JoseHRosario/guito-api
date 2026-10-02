using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Services.Auth;

namespace GuitoApi.Tests;

/// <summary>Stub for the revoke service in HTTP-boundary tests; records calls.</summary>
public class FakeRevokeGoogleTokenService : IRevokeGoogleTokenService
{
    public List<string> Calls { get; } = [];
    public Exception? Throw { get; set; }

    public Task RevokeAsync(LogoutRequest request, CancellationToken cancellationToken = default)
    {
        Calls.Add(request.AccessToken);
        return Throw is null ? Task.CompletedTask : Task.FromException(Throw);
    }
}
