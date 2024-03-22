namespace GuitoApi.Configuration
{
    public class Authentication
    {
        public bool ValidateIdToken { get; set; }
        public List<string> AllowedLogins { get; set; } = [];
        public string OAuthAudience { get; set; } = string.Empty;
    }
}
