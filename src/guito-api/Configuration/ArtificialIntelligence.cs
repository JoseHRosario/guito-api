namespace GuitoApi.Configuration
{
    /// <summary>Artificial-intelligence extraction settings (issue #69). Models are OpenRouter ids.</summary>
    public class ArtificialIntelligence
    {
        /// <summary>Chat model extracting date/amount/description from the prompt (OpenRouter chat completions).</summary>
        public string ExtractModel { get; set; } = "google/gemini-2.5-flash";

        /// <summary>Decision model picking the Category (Jev choice over the sheet's category list).</summary>
        public string CategoryModel { get; set; } = "typesafe/jev-1.13";
    }
}