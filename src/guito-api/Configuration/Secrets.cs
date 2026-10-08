using System.Text.Json;

namespace GuitoApi.Configuration
{
    /// <summary>
    /// Payload of the "guito-api/prod" Secrets Manager secret. Written via
    /// `aws secretsmanager put-secret-value` as one JSON document; never committed.
    /// </summary>
    public class SecretsPayload
    {
        /// <summary>Full Google service-account key (client_email, private_key, ...).</summary>
        public JsonDocument GoogleServiceAccount { get; set; } = JsonDocument.Parse("{}");

        /// <summary>Long-lived agent keys; a match on X-Api-Key authorizes the request.</summary>
        public List<string> ApiKeys { get; set; } = [];

        /// <summary>OpenRouter API key for expense extraction (issue #69).</summary>
        public string OpenRouterApiKey { get; set; } = string.Empty;

        /// <summary>Enable Banking application credentials (issue #89, ADR-0004).</summary>
        public EnableBankingSecrets EnableBanking { get; set; } = new();
    }
}
