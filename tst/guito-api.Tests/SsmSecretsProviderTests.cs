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
        using var client = new SsmFakeClient { Value = "{\"apiKeys\":[\"hermetic-key\"],\"openRouterApiKey\":\"hermetic-router\",\"googleServiceAccount\":{\"project_id\":\"hermetic\"}}" };
        using var services = CreateServices(client);
        var provider = services.GetRequiredService<ISecretsProvider>();
        using var cancellation = new CancellationTokenSource();

        // Act
        var payload = await provider.GetAsync(cancellation.Token);

        // Assert
        Assert.Equal("hermetic-key", Assert.Single(payload.ApiKeys));
        Assert.Equal("hermetic-router", payload.OpenRouterApiKey);
        Assert.Equal("hermetic", payload.GoogleServiceAccount.RootElement.GetProperty("project_id").GetString());
        Assert.Equal("/guito-api/staging", client.Request!.Name);
        Assert.True(client.Request.WithDecryption);
        Assert.Equal(cancellation.Token, client.CancellationToken);
    }

    [Fact]
    public async Task GetAsync_ShouldRefreshAtFiveMinutes_WhenRuntimePayloadIsCachedAsync()
    {
        // Arrange
        using var client = new SsmFakeClient { Value = "{\"apiKeys\":[\"first\"]}" };
        var clock = new SsmFakeTimeProvider();
        using var services = CreateServices(client, clock);
        var provider = services.GetRequiredService<ISecretsProvider>();
        await provider.GetAsync();
        client.Value = "{\"apiKeys\":[\"second\"]}";

        // Act
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        var cached = await provider.GetAsync();
        var cachedCalls = client.Calls;
        clock.Advance(TimeSpan.FromTicks(1));
        var refreshed = await provider.GetAsync();

        // Assert
        Assert.Equal("first", Assert.Single(cached.ApiKeys));
        Assert.Equal(1, cachedCalls);
        Assert.Equal("second", Assert.Single(refreshed.ApiKeys));
        Assert.Equal(2, client.Calls);
    }

    internal static ServiceProvider CreateServices(SsmFakeClient client, TimeProvider? clock = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppConfiguration:Secrets:Location"] = "AwsSsm",
            ["AppConfiguration:Secrets:SecretName"] = "/guito-api/staging",
            ["AppConfiguration:Secrets:HumanAuthSecretName"] = "/guito-api/human-auth",
            ["AppConfiguration:EnableBanking:SecretsSource"] = "AwsSsm",
            ["AppConfiguration:EnableBanking:SsmParameterName"] = "/guito-api/eb-staging-pk",
            ["AppConfiguration:EnableBanking:ApplicationId"] = "hermetic-app"
        }).Build();
        var services = new ServiceCollection();
        new GuitoApi.Startup(configuration).ConfigureServices(services);
        services.AddSingleton<IAmazonSimpleSystemsManagement>(client);
        services.AddSingleton<TimeProvider>(clock ?? TimeProvider.System);
        return services.BuildServiceProvider();
    }
}
