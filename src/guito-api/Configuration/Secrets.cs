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

        private string? _userSessionEncryptionKey;
        private string? _legacyUserSessionEncryptionKey;

        /// <summary>Canonical owner-provisioned base64 AES-256 user-session key. Never logged or returned.</summary>
        public string? UserSessionEncryptionKey
        {
            get => _userSessionEncryptionKey;
            set { _userSessionEncryptionKey = value; HasUserSessionEncryptionKey = true; }
        }

        /// <summary>Legacy backend field alias only; retain until separately approved payload migration.</summary>
        public string? HumanSessionEncryptionKey
        {
            get => _legacyUserSessionEncryptionKey;
            set { _legacyUserSessionEncryptionKey = value; HasLegacyUserSessionEncryptionKey = true; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasUserSessionEncryptionKey { get; private set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasLegacyUserSessionEncryptionKey { get; private set; }

        /// <summary>OpenRouter API key for expense extraction (issue #69).</summary>
        public string OpenRouterApiKey { get; set; } = string.Empty;

        /// <summary>Enable Banking application credentials (issue #89, ADR-0004).</summary>
        public EnableBankingSecrets EnableBanking { get; set; } = new();
    }
}
