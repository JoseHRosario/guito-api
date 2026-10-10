using GuitoApi.Infrastructure.Secrets;
using Microsoft.Extensions.DependencyInjection;
using static GuitoApi.Tests.SsmSecretsProviderTests;

namespace GuitoApi.Tests;

public class SsmHumanAuthSecretProviderTests
{
    [Fact]
    public async Task GetAsync_ShouldReadHumanAuthParameter_WhenLocationIsAwsSsmAsync()
    {
        // Arrange
        using var client = new SsmFakeClient { Value = "{\"clientSecret\":\"hermetic-client\"}" };
        using var services = CreateServices(client);
        using var cancellation = new CancellationTokenSource();
        var provider = services.GetRequiredService<IHumanAuthSecretProvider>();

        // Act
        var secret = await provider.GetAsync(cancellation.Token);

        // Assert
        Assert.Equal("hermetic-client", secret.ClientSecret);
        Assert.Equal("/guito-api/human-auth", client.Request!.Name);
        Assert.True(client.Request.WithDecryption);
        Assert.Equal(cancellation.Token, client.CancellationToken);
    }
}
