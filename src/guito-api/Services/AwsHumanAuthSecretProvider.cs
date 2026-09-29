using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using GuitoApi.Configuration;
using GuitoApi.Exceptions;

namespace GuitoApi.Services
{
    /// <summary>
    /// Human-auth client secret from AWS Secrets Manager ("guito-api/human-auth",
    /// issue #52); AWS implementation. Caches for 5 minutes like AwsSecretsProvider.
    /// </summary>
    public class AwsHumanAuthSecretProvider : IHumanAuthSecretProvider
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private readonly string _secretName;
        private HumanAuthSecret? _cached;
        private DateTimeOffset _cachedAt;

        public AwsHumanAuthSecretProvider(string secretName) => _secretName = secretName;

        public async Task<HumanAuthSecret> GetAsync(CancellationToken cancellationToken = default)
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            // SDK falls back to the AWS_REGION env var when no region is given (set on the Lambda function).
            using var client = new AmazonSecretsManagerClient(new AmazonSecretsManagerConfig());
            var response = await client.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = _secretName,
            }, cancellationToken);
            var secret = JsonSerializer.Deserialize<HumanAuthSecret>(
                response.SecretString,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new ProblemException(500, "Human-auth secret payload is invalid");

            _cached = secret;
            _cachedAt = DateTimeOffset.UtcNow;
            return secret;
        }
    }
}
