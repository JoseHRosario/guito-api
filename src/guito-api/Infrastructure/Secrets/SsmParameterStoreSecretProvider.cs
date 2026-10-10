using System.Text.Json;
using Amazon.SimpleSystemsManagement;
using GuitoApi.Configuration;
using GuitoApi.Exceptions;

namespace GuitoApi.Infrastructure.Secrets;

public sealed class SsmParameterStoreSecretProvider : ISecretsProvider
{
    private readonly SsmParameterStoreCache<SecretsPayload> _cache;

    public SsmParameterStoreSecretProvider(string parameterName, IAmazonSimpleSystemsManagement client, TimeProvider? timeProvider = null)
    {
        _cache = new(parameterName, client, timeProvider ?? TimeProvider.System, raw =>
            JsonSerializer.Deserialize<SecretsPayload>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new ProblemException(500, "Secrets payload is invalid"));
    }

    public Task<SecretsPayload> GetAsync(CancellationToken cancellationToken = default) => _cache.GetAsync(cancellationToken);
}
