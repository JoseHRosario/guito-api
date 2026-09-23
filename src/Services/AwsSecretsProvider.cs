using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using GuitoApi.Configuration;
using GuitoApi.Exceptions;

namespace GuitoApi.Services
{
    /// <summary>Secrets from AWS Secrets Manager; production implementation. Caches for 5 minutes.</summary>
    public class AwsSecretsProvider : ISecretsProvider
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private readonly string _secretName;
        private SecretsPayload? _cached;
        private DateTimeOffset _cachedAt;

        public AwsSecretsProvider(string secretName)
        {
            _secretName = secretName;
        }

        public async Task<SecretsPayload> GetAsync()
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            using var client = new AmazonSecretsManagerClient(Amazon.RegionEndpoint.EUWest1);
            var response = await client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = _secretName,
            });
            var payload = JsonSerializer.Deserialize<SecretsPayload>(
                response.SecretString,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ProblemException(500, "Secrets payload is invalid");

            _cached = payload;
            _cachedAt = DateTimeOffset.UtcNow;
            return payload;
        }
    }
}
