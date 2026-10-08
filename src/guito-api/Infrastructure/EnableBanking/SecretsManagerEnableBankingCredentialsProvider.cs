using System.Text.Json;
using GuitoApi.Configuration;
using Microsoft.Extensions.Options;

namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>
    /// Credentials from a dedicated Secrets Manager secret holding {"pem": …} (issue #89):
    /// the EB key lives outside the runtime payload so the deploy script can re-seed that
    /// payload without touching bank credentials. The application id comes from config
    /// (it is public — the JWT "kid"). Caches for 5 minutes like AwsSecretsProvider.
    /// </summary>
    public class SecretsManagerEnableBankingCredentialsProvider : IEnableBankingCredentialsProvider
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private readonly IOptions<EnableBankingOptions> _options;
        private readonly Func<string, CancellationToken, Task<string>> _getSecretValue;
        private EnableBankingCredentials? _cached;
        private DateTimeOffset _cachedAt;

        /// <param name="getSecretValue">secretId → raw secret string; wired to Secrets Manager in Startup.</param>
        public SecretsManagerEnableBankingCredentialsProvider(
            IOptions<EnableBankingOptions> options,
            Func<string, CancellationToken, Task<string>> getSecretValue)
        {
            _options = options;
            _getSecretValue = getSecretValue;
        }

        public async Task<EnableBankingCredentials> GetAsync(CancellationToken cancellationToken = default)
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            var options = _options.Value;
            if (string.IsNullOrWhiteSpace(options.ApplicationId))
                throw new InvalidOperationException("AppConfiguration:EnableBanking:ApplicationId is required.");
            if (string.IsNullOrWhiteSpace(options.SecretsManagerSecretName))
                throw new InvalidOperationException("AppConfiguration:EnableBanking:SecretsManagerSecretName is required.");

            var raw = await _getSecretValue(options.SecretsManagerSecretName, cancellationToken);
            var pem = JsonDocument.Parse(raw).RootElement.GetProperty("pem").GetString()
                ?? throw new InvalidOperationException($"Secret '{options.SecretsManagerSecretName}' has no 'pem' property.");

            _cached = new EnableBankingCredentials(options.ApplicationId, NormalizePem(pem));
            _cachedAt = DateTimeOffset.UtcNow;
            return _cached;
        }

        /// <summary>
        /// Tolerates PEMs stored flattened to one line (the staging key arrived that way):
        /// RSA.ImportFromPem needs BEGIN/END on their own lines and 64-char base64 rows.
        /// </summary>
        public static string NormalizePem(string pem)
        {
            var base64 = string.Concat(pem
                .Replace("-----BEGIN PRIVATE KEY-----", " ")
                .Replace("-----END PRIVATE KEY-----", " ")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.StartsWith("-----")));
            return "-----BEGIN PRIVATE KEY-----\n"
                + string.Join('\n', Enumerable.Range(0, (int)Math.Ceiling(base64.Length / 64.0))
                    .Select(i => base64.Substring(i * 64, Math.Min(64, base64.Length - i * 64))))
                + "\n-----END PRIVATE KEY-----\n";
        }
    }
}