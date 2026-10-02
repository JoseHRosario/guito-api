using GuitoApi.Infrastructure.AI;

namespace GuitoApi.Tests
{
    /// <summary>Injects the fake OpenRouter HTTP handler through the provider seam.</summary>
    public class FakeOpenRouterClientProvider(FakeOpenRouterHttpHandler handler) : IOpenRouterClientProvider
    {
        public Task<HttpClient> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HttpClient(handler));
    }
}
