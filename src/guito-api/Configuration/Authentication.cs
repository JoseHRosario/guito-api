namespace GuitoApi.Configuration
{
    public class Authentication
    {
        public bool ValidateIdToken { get; set; }
        public List<string> AllowedLogins { get; set; } = [];
        public string OAuthAudience { get; set; } = string.Empty;

        /// <summary>Agent key path (ADR-0003): require a valid X-Api-Key on non-public endpoints.</summary>
        public bool ValidateApiKey { get; set; }
    }
}
