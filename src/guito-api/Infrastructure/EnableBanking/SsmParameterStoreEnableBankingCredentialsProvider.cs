using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using GuitoApi.Configuration;
using Microsoft.Extensions.Options;

namespace GuitoApi.Infrastructure.EnableBanking;

public sealed class SsmParameterStoreEnableBankingCredentialsProvider : IEnableBankingCredentialsProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _parameterName;
    private readonly DedicatedPemEnableBankingCredentialsProvider _credentials;

    public SsmParameterStoreEnableBankingCredentialsProvider(IOptions<EnableBankingOptions> options, IAmazonSimpleSystemsManagement client, TimeProvider? timeProvider = null)
    {
        var value = options.Value;
        _parameterName = value.SsmParameterName;
        _credentials = new(value.ApplicationId, value.SsmParameterName, async (name, cancellationToken) =>
        {
            var response = await client.GetParameterAsync(new GetParameterRequest { Name = name, WithDecryption = true }, cancellationToken);
            return response.Parameter.Value;
        }, timeProvider);
    }

    public async Task<EnableBankingCredentials> GetAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_parameterName))
            throw new InvalidOperationException("AppConfiguration:EnableBanking:SsmParameterName is required.");
        await _gate.WaitAsync(cancellationToken);
        try { return await _credentials.GetAsync(cancellationToken); }
        finally { _gate.Release(); }
    }
}
