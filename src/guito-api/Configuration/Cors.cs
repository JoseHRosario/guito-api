namespace GuitoApi.Configuration
{
    /// <summary>Browser origins allowed to call the API cross-origin (CORS).</summary>
    public class Cors
    {
        public List<string> AllowedOrigins { get; set; } = [];
    }
}
