using GuitoApi.Configuration;
using GuitoApi.Services;

namespace GuitoApi.Tests;

/// <summary>Canned secrets payload; tests replace ISecretsProvider with this via DI.</summary>
public class FakeSecretsProvider : ISecretsProvider
{
    public SecretsPayload Payload { get; set; } = new()
    {
        ApiKeys = ["test-agent-key"],
    };

    public Task<SecretsPayload> GetAsync() => Task.FromResult(Payload);
}
