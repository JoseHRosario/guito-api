using GuitoApi.Configuration;

namespace GuitoApi.Services
{
    /// <summary>
    /// Single seam over runtime secrets (issue #3). Implementations: AwsSecretsProvider
    /// (Secrets Manager, production) and FileSecretsProvider (gitignored local file).
    /// </summary>
    public interface ISecretsProvider
    {
        Task<SecretsPayload> GetAsync(CancellationToken cancellationToken = default);
    }
}
