using System.Text.Json;
using Amazon.SimpleSystemsManagement;
using GuitoApi.Configuration;
using GuitoApi.Exceptions;

namespace GuitoApi.Infrastructure.Secrets;

public sealed class SsmParameterStoreHumanAuthSecretProvider : IHumanAuthSecretProvider
{
    private readonly SsmParameterStoreCache<HumanAuthSecret> _cache;

    public SsmParameterStoreHumanAuthSecretProvider(string parameterName, IAmazonSimpleSystemsManagement client, TimeProvider? timeProvider = null)
    {
        _cache = new(parameterName, client, timeProvider ?? TimeProvider.System, raw =>
            JsonSerializer.Deserialize<HumanAuthSecret>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ProblemException(500, "Human-auth secret payload is invalid"));
    }

    public Task<HumanAuthSecret> GetAsync(CancellationToken cancellationToken = default) => _cache.GetAsync(cancellationToken);
}
