using GuitoApi.Configuration;
using GuitoApi.Services;

using GuitoApi.Infrastructure.Secrets;

namespace GuitoApi.Tests;

/// <summary>Canned secrets payload; tests replace ISecretsProvider with this via DI.</summary>
public class FakeSecretsProvider : ISecretsProvider
{
    public SecretsPayload Payload { get; set; } = new()
    {
        ApiKeys = ["test-agent-key"],
        OpenRouterApiKey = "test-openrouter-key",
    };

    public Task<SecretsPayload> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Payload);
}
