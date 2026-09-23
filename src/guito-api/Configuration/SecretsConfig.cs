namespace GuitoApi.Configuration
{
    /// <summary>Where runtime secrets come from (issue #3).</summary>
    public class SecretsConfig
    {
        /// <summary>"Aws" = AWS Secrets Manager; "File" = gitignored local JSON file.</summary>
        public string Location { get; set; } = string.Empty;

        /// <summary>Secrets Manager secret name (e.g. guito-api/prod).</summary>
        public string SecretName { get; set; } = string.Empty;

        /// <summary>Local secrets file path, resolved against the project directory.</summary>
        public string FilePath { get; set; } = string.Empty;
    }
}
