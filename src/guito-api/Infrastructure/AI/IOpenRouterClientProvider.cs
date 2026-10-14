using GuitoApi.Configuration;

namespace GuitoApi.Infrastructure.AI
{
    /// <summary>
    /// HttpClient seam over the OpenRouter API (chat completions + Decisions API),
    /// faked at the HTTP-transport level in hermetic tests.
    /// </summary>
    public interface IOpenRouterClientProvider
    {
        Task<HttpClient> GetAsync(CancellationToken cancellationToken = default);
    }
}
