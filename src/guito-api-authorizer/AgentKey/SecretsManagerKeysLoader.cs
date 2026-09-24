using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace GuitoApiAuthorizer.AgentKey
{
    /// <summary>
    /// Loads ApiKeys from the same Secrets Manager secret the API uses
    /// ("guito-api/prod", JSON document with an ApiKeys array). Caches for 5 min.
    /// </summary>
    public class SecretsManagerKeysLoader(string secretName) : IKeysLoader
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private IReadOnlyList<string>? _cached;
        private DateTimeOffset _cachedAt;

        public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            using var client = new AmazonSecretsManagerClient(new AmazonSecretsManagerConfig()); // region from AWS_REGION env var
            var response = await client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = secretName,
            }, cancellationToken);

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