using Amazon;
using Amazon.Runtime;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using GuitoApiAuthorizer.AgentKey;

namespace GuitoApiAuthorizer.Tests;

public class SsmParameterStoreKeysLoaderTests
{
    [Fact]
    public async Task LoadAsync_ShouldReadDecryptedApiKeys_WhenParameterContainsPayload()
    {
        // Arrange
        using var client = new FakeSsmClient();
        using var cancellation = new CancellationTokenSource();
        IKeysLoader loader = new SsmParameterStoreKeysLoader("/guito-api/staging", client);

        // Act
        var keys = await loader.LoadAsync(cancellation.Token);

        // Assert
        Assert.Equal(new[] { "alpha", "beta", " " }, keys);
        Assert.Equal("/guito-api/staging", client.Request!.Name);
        Assert.True(client.Request.WithDecryption);
        Assert.Equal(cancellation.Token, client.Token);
    }

    [Fact]
    public async Task LoadAsync_ShouldReuseKeysUntilFiveMinutes_WhenParameterChanges()
    {
        // Arrange
        using var client = new FakeSsmClient();
        var clock = new FakeTimeProvider();
        IKeysLoader loader = new SsmParameterStoreKeysLoader("/guito-api/staging", client, clock);
        var original = await WarmAndRotateAsync(loader, client, clock);

        // Act
        var cached = await loader.LoadAsync();
        clock.Advance(TimeSpan.FromTicks(1));
        var refreshed = await loader.LoadAsync();

        // Assert
        Assert.Same(original, cached);
        Assert.Equal(new[] { "rotated" }, refreshed);
        Assert.Equal(2, client.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"ApiKeys\":null}")]
    [InlineData("{\"ApiKeys\":\"alpha\"}")]
    [InlineData("{\"ApiKeys\":[42]}")]
    public async Task ValidateAsync_ShouldDeny_WhenParameterPayloadIsInvalid(string payload)
    {
        // Arrange
        using var client = new FakeSsmClient { Value = payload };
        var validator = new AgentKeyValidator(new SsmParameterStoreKeysLoader("/guito-api/staging", client));

        // Act
        var result = await validator.ValidateAsync("alpha");

        // Assert
        Assert.False(result.Valid);
        Assert.StartsWith("could not load agent keys:", result.FailureReason);
    }

    [Fact]
    public async Task LoadAsync_ShouldReturnEmptyKeys_WhenApiKeysArrayIsEmpty()
    {
        // Arrange
        using var client = new FakeSsmClient { Value = """{"ApiKeys":[]}""" };
        var loader = new SsmParameterStoreKeysLoader("/guito-api/staging", client);

        // Act
        var keys = await loader.LoadAsync();

        // Assert
        Assert.Empty(keys);
    }

    [Fact]
    public async Task LoadAsync_ShouldPropagateCancellation_WhenRequestIsCancelled()
    {
        // Arrange
        using var client = new FakeSsmClient();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var loader = new SsmParameterStoreKeysLoader("/guito-api/staging", client);

        // Act
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loader.LoadAsync(cancellation.Token));

        // Assert
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(cancellation.Token, client.Token);
    }

    [Fact]
    public async Task LoadAsync_ShouldRetryRefresh_WhenExpiredParameterCannotBeLoaded()
    {
        // Arrange
        using var client = new FakeSsmClient();
        var clock = new FakeTimeProvider();
        IKeysLoader loader = new SsmParameterStoreKeysLoader("/guito-api/staging", client, clock);
        await WarmAndExpireAsync(loader, client, clock);

        // Act
        var denied = await new AgentKeyValidator(loader).ValidateAsync("alpha");
        client.Error = null;
        client.Value = """{"ApiKeys":["rotated"]}""";
        var refreshed = await loader.LoadAsync();

        // Assert
        Assert.False(denied.Valid);
        Assert.Equal(new[] { "rotated" }, refreshed);
        Assert.Equal(3, client.Calls);
    }

    private static async Task<IReadOnlyList<string>> WarmAndRotateAsync(IKeysLoader loader, FakeSsmClient client, FakeTimeProvider clock)
    {
        var original = await loader.LoadAsync();
        client.Value = """{"ApiKeys":["rotated"]}""";
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        return original;
    }

    private static async Task WarmAndExpireAsync(IKeysLoader loader, FakeSsmClient client, FakeTimeProvider clock)
    {
        await loader.LoadAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        client.Error = new ParameterNotFoundException("Parameter not found");
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FakeSsmClient() : AmazonSimpleSystemsManagementClient(
        new BasicAWSCredentials("hermetic", "hermetic"),
        new AmazonSimpleSystemsManagementConfig { RegionEndpoint = RegionEndpoint.EUWest1 })
    {
        public GetParameterRequest? Request { get; private set; }
        public CancellationToken Token { get; private set; }
        public int Calls { get; private set; }
        public Exception? Error { get; set; }
        public string Value { get; set; } = """{"ApiKeys":["alpha",null,"","beta"," "],"Other":"ignored"}""";

        public override Task<GetParameterResponse> GetParameterAsync(
            GetParameterRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            Token = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (Error is not null)
                return Task.FromException<GetParameterResponse>(Error);
            return Task.FromResult(new GetParameterResponse { Parameter = new Parameter { Value = Value } });
        }
    }
}
