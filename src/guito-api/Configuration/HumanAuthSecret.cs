namespace GuitoApi.Configuration
{
    /// <summary>Payload of the "guito-api/human-auth" Secrets Manager secret; never committed.</summary>
    public class HumanAuthSecret
    {
        /// <summary>Google OAuth client secret of the Web-app client; never logged or returned.</summary>
        public string ClientSecret { get; set; } = string.Empty;
    }
}
