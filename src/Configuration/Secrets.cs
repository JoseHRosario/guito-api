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
    }
}
