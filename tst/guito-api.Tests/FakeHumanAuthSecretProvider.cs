using GuitoApi.Configuration;
using GuitoApi.Services;

using GuitoApi.Infrastructure.Secrets;

namespace GuitoApi.Tests;

/// <summary>Canned human-auth secret; tests replace IHumanAuthSecretProvider with this via DI.</summary>
public class FakeHumanAuthSecretProvider : IHumanAuthSecretProvider
{
    public HumanAuthSecret Secret { get; set; } = new()
    {
        ClientSecret = "fake-client-secret",
    };

    public Task<HumanAuthSecret> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Secret);
}
