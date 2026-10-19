using GuitoApi.Model;

namespace GuitoApi.Repositories;

public interface IUserSessionRepository
{
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);
    Task CreateAsync(UserSession session, CancellationToken cancellationToken = default);
    Task<UserSession?> AcquireAsync(string handleHash, string lease, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<bool> CompleteAsync(string handleHash, string lease, string encryptedCredential, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    Task ReleaseAsync(string handleHash, string lease, CancellationToken cancellationToken = default);
    Task<bool> InvalidateAsync(string handleHash, string lease, CancellationToken cancellationToken = default);
    Task DeleteAsync(string handleHash, CancellationToken cancellationToken = default);
}
