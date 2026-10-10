using GuitoApi.Configuration;

namespace GuitoApi.Infrastructure.Secrets
{
    /// <summary>Runtime credentials from SSM SecureString or a gitignored local JSON file.</summary>
    public interface ISecretsProvider
    {
        Task<SecretsPayload> GetAsync(CancellationToken cancellationToken = default);
    }
}
