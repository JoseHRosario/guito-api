namespace GuitoApi.Infrastructure.AI
{
    /// <summary>Named HttpClient for OpenRouter traffic (issue #69).</summary>
    public class OpenRouterClientProvider(IHttpClientFactory httpClientFactory) : IOpenRouterClientProvider
    {
        public const string HttpClientName = "OpenRouter";

        public Task<HttpClient> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(httpClientFactory.CreateClient(HttpClientName));
    }
}
