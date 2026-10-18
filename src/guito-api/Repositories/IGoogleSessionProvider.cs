using GuitoApi.Model;

namespace GuitoApi.Repositories;

public interface IGoogleSessionProvider
{
    Task<HumanSessionTokens> ExchangeAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken = default);
    Task<HumanSessionTokens> RefreshAsync(string credential, CancellationToken cancellationToken = default);
    Task<HumanSessionIdentity> ValidateAsync(string idToken, string? expectedSubject = null, CancellationToken cancellationToken = default);
}
