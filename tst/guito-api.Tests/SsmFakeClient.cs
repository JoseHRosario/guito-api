using Amazon;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace GuitoApi.Tests;

internal sealed class SsmFakeClient() : AmazonSimpleSystemsManagementClient(
    new BasicAWSCredentials("hermetic", "hermetic"), new AmazonSimpleSystemsManagementConfig { RegionEndpoint = RegionEndpoint.EUWest1 })
{
    public string Value { get; set; } = "{}";
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }
    public GetParameterRequest? Request { get; private set; }
    public CancellationToken CancellationToken { get; private set; }

    public override Task<GetParameterResponse> GetParameterAsync(GetParameterRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        Request = request;
        CancellationToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is not null) return Task.FromException<GetParameterResponse>(Failure);
        return Task.FromResult(new GetParameterResponse { Parameter = new Parameter { Value = Value } });
    }
}
