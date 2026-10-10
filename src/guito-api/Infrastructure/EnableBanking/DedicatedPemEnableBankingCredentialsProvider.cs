using System.Text.Json;

namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>Backend-neutral dedicated PEM parsing and five-minute credential cache.</summary>
    public class DedicatedPemEnableBankingCredentialsProvider : IEnableBankingCredentialsProvider
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

        private readonly string _applicationId;
        private readonly string _sourceName;
        private readonly Func<string, CancellationToken, Task<string>> _loadValueAsync;
        private EnableBankingCredentials? _cached;
        private DateTimeOffset _cachedAt;
        private readonly TimeProvider _timeProvider;

        public DedicatedPemEnableBankingCredentialsProvider(
            string applicationId, string sourceName,
            Func<string, CancellationToken, Task<string>> loadValueAsync,
            TimeProvider? timeProvider = null)
        {
            _applicationId = applicationId;
            _sourceName = sourceName;
            _loadValueAsync = loadValueAsync;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public async Task<EnableBankingCredentials> GetAsync(CancellationToken cancellationToken = default)
        {
            if (_cached is not null && _timeProvider.GetUtcNow() - _cachedAt < CacheTtl)
                return _cached;

            ValidateConfiguration();

            var raw = await _loadValueAsync(_sourceName, cancellationToken);
            var pem = ExtractPem(raw)
                ?? throw new InvalidOperationException(
                    $"Key source '{_sourceName}' carries neither a raw PEM nor a '{{\"pem\": …}}' JSON object.");

            _cached = new EnableBankingCredentials(_applicationId, NormalizePem(pem));
            _cachedAt = _timeProvider.GetUtcNow();
            return _cached;
        }

        private void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(_applicationId))
                throw new InvalidOperationException("AppConfiguration:EnableBanking:ApplicationId is required.");
            if (string.IsNullOrWhiteSpace(_sourceName))
                throw new InvalidOperationException("AppConfiguration:EnableBanking:dedicated key source name is required.");
        }

        /// <summary>Accepts both key layouts: JSON {"pem": …} (parsed first), or the raw PEM text.</summary>
        private static string? ExtractPem(string raw)
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                if (document.RootElement.TryGetProperty("pem", out var pem))
                    return pem.GetString();
            }
            catch (JsonException)
            {
                // Not JSON — fall through to the raw-PEM layout.
            }

            return raw.Contains("-----BEGIN PRIVATE KEY-----") ? raw : null;
        }

        /// <summary>
        /// Tolerates PEMs stored flattened to one line (the staging key arrived that way):
        /// RSA.ImportFromPem needs BEGIN/END on their own lines and 64-char base64 rows.
        /// </summary>
        private static string NormalizePem(string pem)
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