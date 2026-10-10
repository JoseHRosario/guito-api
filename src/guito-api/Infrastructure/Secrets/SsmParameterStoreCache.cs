using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace GuitoApi.Infrastructure.Secrets;

internal sealed class SsmParameterStoreCache<T>(string parameterName, IAmazonSimpleSystemsManagement client, TimeProvider clock, Func<string, T> deserialize) where T : class
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private T? _cached;
    private DateTimeOffset _expiresAt;

    public async Task<T> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && clock.GetUtcNow() < _expiresAt) return _cached;
            var response = await client.GetParameterAsync(new GetParameterRequest { Name = parameterName, WithDecryption = true }, cancellationToken);
            _cached = deserialize(response.Parameter.Value);
            _expiresAt = clock.GetUtcNow().AddMinutes(5);
            return _cached;
        }
        finally { _gate.Release(); }
    }
}
