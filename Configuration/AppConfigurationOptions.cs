namespace GuitoApi.Configuration
{
    public class AppConfigurationOptions
    {
        public const string AppConfiguration = "AppConfiguration";
        public Googlesheets Googlesheets { get; set; } = new();
        public bool ValidateIdToken { get; set; }
        public List<string> AllowedLogins { get; set; } = [];
        public string OAuthAudience { get; set; } = string.Empty;
    }

    public class Googlesheets
    {
        public string SpreadsheetId { get; set; } = string.Empty;
        public string ExpensesRange { get; set; } = string.Empty;
        public string ExpensesDateRange { get; set; } = string.Empty;
        public string CategoriesRange { get; set; } = string.Empty;
    }
}
