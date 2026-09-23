using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace GuitoApiAuthorizer
{
    /// <summary>Agent keys for the X-Api-Key authorizer (issue #4/#3).</summary>
    public interface IKeysLoader
    {
        Task<IReadOnlyList<string>> LoadAsync();
    }

    /// <summary>
    /// Loads ApiKeys from the same Secrets Manager secret the API uses
    /// ("guito-api/prod", JSON document with an ApiKeys array). Caches for 5 min.
    /// </summary>
    public class SecretsManagerKeysLoader : IKeysLoader
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private readonly string _secretName;
        private IReadOnlyList<string>? _cached;
        private DateTimeOffset _cachedAt;

        public SecretsManagerKeysLoader(string secretName)
        {
            _secretName = secretName;
        }

        public async Task<IReadOnlyList<string>> LoadAsync()
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            using var client = new AmazonSecretsManagerClient(new AmazonSecretsManagerConfig()); // region from AWS_REGION env var
            var response = await client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = _secretName,
            });

            using var document = JsonDocument.Parse(response.SecretString);
            var keys = document.RootElement
                .GetProperty("ApiKeys")
                .EnumerateArray()
                .Select(k => k.GetString())
                .Where(k => !string.IsNullOrEmpty(k))
                .Select(k => k!)
                .ToList();

            _cached = keys;
            _cachedAt = DateTimeOffset.UtcNow;
            return keys;
        }
    }
}
