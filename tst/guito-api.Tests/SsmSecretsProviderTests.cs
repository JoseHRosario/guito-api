using Amazon.SimpleSystemsManagement;
using GuitoApi.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

public class SsmSecretsProviderTests
{
    [Fact]
    public async Task GetAsync_ShouldReadDecryptedRuntimePayload_WhenLocationIsAwsSsmAsync()
    {
        // Arrange
        using var fixture = new RuntimeFixture("{\"apiKeys\":[\"hermetic-key\"],\"openRouterApiKey\":\"hermetic-router\",\"googleServiceAccount\":{\"project_id\":\"hermetic\"}}");
        using var cancellation = new CancellationTokenSource();

        // Act
        var payload = await fixture.Provider.GetAsync(cancellation.Token);

        // Assert
        Assert.Equal("hermetic-key", Assert.Single(payload.ApiKeys));
        Assert.Equal("hermetic-router", payload.OpenRouterApiKey);
        Assert.Equal("hermetic", payload.GoogleServiceAccount.RootElement.GetProperty("project_id").GetString());
        Assert.Equal("/guito-api/staging", fixture.Client.Request!.Name);
        Assert.True(fixture.Client.Request.WithDecryption);
        Assert.Equal(cancellation.Token, fixture.Client.CancellationToken);
    }

    [Fact]
    public async Task GetAsync_ShouldRefreshAtFiveMinutes_WhenRuntimePayloadIsCachedAsync()
    {
        // Arrange
        using var fixture = new RuntimeFixture("{\"apiKeys\":[\"first\"]}");
        await fixture.WarmAndRotateAsync("{\"apiKeys\":[\"second\"]}");

        // Act
        fixture.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        var cached = await fixture.Provider.GetAsync();
        var cachedCalls = fixture.Client.Calls;
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        var refreshed = await fixture.Provider.GetAsync();

        // Assert
        Assert.Equal("first", Assert.Single(cached.ApiKeys));
        Assert.Equal(1, cachedCalls);
        Assert.Equal("second", Assert.Single(refreshed.ApiKeys));
        Assert.Equal(2, fixture.Client.Calls);
    }

    private sealed class RuntimeFixture : IDisposable
    {
        public SsmFakeClient Client { get; }
        public SsmFakeTimeProvider Clock { get; } = new();
        private readonly ServiceProvider _services;
        public ISecretsProvider Provider => _services.GetRequiredService<ISecretsProvider>();

        public RuntimeFixture(string payload)
        {
            Client = new SsmFakeClient { Value = payload };
            _services = CreateServices(Client, Clock);
        }

        public async Task WarmAndRotateAsync(string payload)
        {
            await Provider.GetAsync();
            Client.Value = payload;
        }

        public void Dispose()
        {
            _services.Dispose();
            Client.Dispose();
        }
    }

    internal static ServiceProvider CreateServices(SsmFakeClient client, TimeProvider? clock = null)
    {
        var configuration = CreateConfiguration();
        var services = new ServiceCollection();
        new GuitoApi.Startup(configuration).ConfigureServices(services);
        services.AddSingleton<IAmazonSimpleSystemsManagement>(client);
        services.AddSingleton<TimeProvider>(clock ?? TimeProvider.System);
        return services.BuildServiceProvider();
    }

    private static IConfiguration CreateConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppConfiguration:Secrets:Location"] = "AwsSsm",
            ["AppConfiguration:Secrets:SecretName"] = "/guito-api/staging",
            ["AppConfiguration:Secrets:HumanAuthSecretName"] = "/guito-api/human-auth",
            ["AppConfiguration:EnableBanking:SecretsSource"] = "AwsSsm",
            ["AppConfiguration:EnableBanking:SsmParameterName"] = "/guito-api/eb-staging-pk",
            ["AppConfiguration:EnableBanking:ApplicationId"] = "hermetic-app"
        }).Build();
}
