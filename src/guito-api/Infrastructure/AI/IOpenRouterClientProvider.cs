using GuitoApi.Configuration;

namespace GuitoApi.Infrastructure.AI
{
    /// <summary>
    /// HttpClient seam over the OpenRouter API (chat completions + Decisions API),
    /// mirroring the Sheets provider shape so tests fake it at the HTTP-transport level.
    /// </summary>
    public interface IOpenRouterClientProvider
    {
        Task<HttpClient> GetAsync(CancellationToken cancellationToken = default);
    }
}
