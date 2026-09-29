namespace GuitoApi.Configuration
{
    /// <summary>Where runtime secrets come from (issue #3).</summary>
    public class SecretsConfig
    {
        /// <summary>Secrets:Location value selecting AWS Secrets Manager.</summary>
        public const string LocationAws = "Aws";

        /// <summary>"Aws" = AWS Secrets Manager; anything else = gitignored local JSON file.</summary>
        public string Location { get; set; } = string.Empty;

        /// <summary>Secrets Manager secret name (e.g. guito-api/prod).</summary>
        public string SecretName { get; set; } = string.Empty;

        /// <summary>Local secrets file path, resolved against the project directory.</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>Secrets Manager secret holding the Google human-auth client secret (issue #52).</summary>
        public string HumanAuthSecretName { get; set; } = "guito-api/human-auth";

        /// <summary>Local human-auth secret file path, resolved against the project directory (issue #52).</summary>
        public string HumanAuthFilePath { get; set; } = "human-auth.local.json";
    }
}
