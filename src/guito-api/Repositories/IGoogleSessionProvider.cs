using GuitoApi.Model;

namespace GuitoApi.Repositories;

public interface IGoogleSessionProvider
{
    Task<UserSessionTokens> ExchangeAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken = default);
    Task<UserSessionTokens> RefreshAsync(string credential, CancellationToken cancellationToken = default);
    Task<UserSessionIdentity> ValidateAsync(string idToken, string? expectedSubject = null, CancellationToken cancellationToken = default);
}
