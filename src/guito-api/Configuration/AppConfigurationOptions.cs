namespace GuitoApi.Configuration
{
    public class AppConfigurationOptions
    {
        public const string AppConfiguration = "AppConfiguration";
        public Googlesheets Googlesheets { get; set; } = new();
        public Authentication Authentication { get; set; } = new();
        public Cors Cors { get; set; } = new();
        public Nordigen Nordigen { get; set; } = new();
        public ArtificialIntelligence ArtificialIntelligence { get; set; } = new();
        public SecretsConfig Secrets { get; set; } = new();
    }
}
