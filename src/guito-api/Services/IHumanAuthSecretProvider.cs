using GuitoApi.Configuration;

namespace GuitoApi.Services
{
    /// <summary>Google human-auth client secret (issue #52): from Secrets Manager in AWS environments, gitignored local file in dev.</summary>
    public interface IHumanAuthSecretProvider
    {
        Task<HumanAuthSecret> GetAsync(CancellationToken cancellationToken = default);
    }
}
