namespace GuitoApi.Configuration
{
    public class AppConfigurationOptions
    {
        public const string AppConfiguration = "AppConfiguration";
        public Googlesheets Googlesheets { get; set; } = new();
        public Authentication Authentication { get; set; } = new();
    }

   
}
