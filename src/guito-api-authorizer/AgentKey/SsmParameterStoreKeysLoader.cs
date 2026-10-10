using System.Text.Json;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace GuitoApiAuthorizer.AgentKey;

public class SsmParameterStoreKeysLoader(
    string parameterName, IAmazonSimpleSystemsManagement client, TimeProvider? timeProvider = null) : IKeysLoader
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private IReadOnlyList<string>? _cached;
    private DateTimeOffset _cachedAt;

    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null && _timeProvider.GetUtcNow() - _cachedAt < CacheTtl)
            return _cached;

        var response = await client.GetParameterAsync(
            new GetParameterRequest { Name = parameterName, WithDecryption = true }, cancellationToken);
        var keys = ParseKeys(response.Parameter.Value);
        _cached = keys;
        _cachedAt = _timeProvider.GetUtcNow();
        return keys;
    }

    private static IReadOnlyList<string> ParseKeys(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("ApiKeys").EnumerateArray()
            .Select(key => key.GetString())
            .Where(key => !string.IsNullOrEmpty(key))
            .Select(key => key!)
            .ToList();
    }
}
