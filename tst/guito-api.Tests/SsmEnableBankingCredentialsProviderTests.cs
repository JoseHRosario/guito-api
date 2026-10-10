using System.Security.Cryptography;
using System.Text.Json;
using GuitoApi.Infrastructure.EnableBanking;
using Microsoft.Extensions.DependencyInjection;
using static GuitoApi.Tests.SsmSecretsProviderTests;

namespace GuitoApi.Tests;

public class SsmEnableBankingCredentialsProviderTests
{
    [Fact]
    public async Task GetAsync_ShouldReportMissingParameterName_WhenDedicatedNameIsUnsetAsync()
    {
        // Arrange
        using var client = new SsmFakeClient();
        var options = Microsoft.Extensions.Options.Options.Create(new GuitoApi.Configuration.EnableBankingOptions { ApplicationId = "hermetic-app" });
        IEnableBankingCredentialsProvider provider = new SsmParameterStoreEnableBankingCredentialsProvider(options, client);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync());

        // Assert
        Assert.Contains("SsmParameterName", exception.Message);
        Assert.Equal(0, client.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_ShouldNormalizeDedicatedKey_WhenSourceIsAwsSsmAsync(bool jsonWrapped)
    {
        // Arrange
        using var key = RSA.Create(2048);
        var pem = key.ExportPkcs8PrivateKeyPem();
        var flattened = pem.Replace('\n', ' ');
        using var client = new SsmFakeClient { Value = jsonWrapped ? JsonSerializer.Serialize(new { pem = flattened }) : flattened };
        using var services = CreateServices(client);
        using var scope = services.CreateScope();
        using var cancellation = new CancellationTokenSource();
        var provider = scope.ServiceProvider.GetRequiredService<IEnableBankingCredentialsProvider>();

        // Act
        var credentials = await provider.GetAsync(cancellation.Token);

        // Assert
        Assert.Equal("hermetic-app", credentials.ApplicationId);
        using var imported = RSA.Create();
        imported.ImportFromPem(credentials.PrivateKey);
        Assert.Equal(key.ExportRSAPublicKey(), imported.ExportRSAPublicKey());
        Assert.Equal("/guito-api/eb-staging-pk", client.Request!.Name);
        Assert.True(client.Request.WithDecryption);
        Assert.Equal(cancellation.Token, client.CancellationToken);
    }
}
