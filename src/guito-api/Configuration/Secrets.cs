namespace GuitoApi.Configuration
{
    /// <summary>
    /// Runtime payload stored in an environment's SSM SecureString parameter
    /// or a gitignored local JSON file; never committed.
    /// </summary>
    public class SecretsPayload
    {
        /// <summary>Long-lived agent keys; a match on X-Api-Key authorizes the request.</summary>
        public List<string> ApiKeys { get; set; } = [];

        /// <summary>Owner-provisioned per-environment base64 AES-256 session key. Never logged or returned.</summary>
        public string HumanSessionEncryptionKey { get; set; } = string.Empty;

        /// <summary>OpenRouter API key for expense extraction (issue #69).</summary>
        public string OpenRouterApiKey { get; set; } = string.Empty;

        /// <summary>Enable Banking application credentials (issue #89, ADR-0004).</summary>
        public EnableBankingSecrets EnableBanking { get; set; } = new();
    }
}
