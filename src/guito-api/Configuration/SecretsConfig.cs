namespace GuitoApi.Configuration
{
    /// <summary>Where runtime secrets come from (issue #3).</summary>
    public class SecretsConfig
    {
        /// <summary>Secrets:Location value selecting AWS Secrets Manager.</summary>
        public const string LocationAws = "Aws";

        public const string LocationAwsSsm = "AwsSsm";

        /// <summary>"Aws" = Secrets Manager; "AwsSsm" = Parameter Store; otherwise a local JSON file.</summary>
        public string Location { get; set; } = string.Empty;

        /// <summary>Secret name or SSM parameter name (e.g. /guito-api/prod for AwsSsm).</summary>
        public string SecretName { get; set; } = string.Empty;

        /// <summary>Local secrets file path, resolved against the project directory.</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>Secret or SSM parameter holding the Google human-auth client secret.</summary>
        public string HumanAuthSecretName { get; set; } = "guito-api/human-auth";

        /// <summary>Local human-auth secret file path, resolved against the project directory (issue #52).</summary>
        public string HumanAuthFilePath { get; set; } = "human-auth.local.json";
    }
}
