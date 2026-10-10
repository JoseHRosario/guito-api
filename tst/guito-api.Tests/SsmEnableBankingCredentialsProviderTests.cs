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

    private static byte[] ImportPublicKey(string pem)
    {
        using var imported = RSA.Create();
        imported.ImportFromPem(pem);
        return imported.ExportRSAPublicKey();
    }

    private sealed class BankFixture : IDisposable
    {
        public RSA Key { get; } = RSA.Create(2048);
        public SsmFakeClient Client { get; }
        private readonly ServiceProvider _services;
        private readonly IServiceScope _scope;
        public IEnableBankingCredentialsProvider Provider => _scope.ServiceProvider.GetRequiredService<IEnableBankingCredentialsProvider>();

        public BankFixture(bool jsonWrapped)
        {
            var flattened = Key.ExportPkcs8PrivateKeyPem().Replace('\n', ' ');
            Client = new SsmFakeClient { Value = jsonWrapped ? JsonSerializer.Serialize(new { pem = flattened }) : flattened };
            _services = CreateServices(Client);
            _scope = _services.CreateScope();
        }

        public void Dispose()
        {
            _scope.Dispose();
            _services.Dispose();
            Client.Dispose();
            Key.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_ShouldNormalizeDedicatedKey_WhenSourceIsAwsSsmAsync(bool jsonWrapped)
    {
        // Arrange
        using var fixture = new BankFixture(jsonWrapped);
        using var cancellation = new CancellationTokenSource();

        // Act
        var credentials = await fixture.Provider.GetAsync(cancellation.Token);

        // Assert
        Assert.Equal("hermetic-app", credentials.ApplicationId);
        Assert.Equal(fixture.Key.ExportRSAPublicKey(), ImportPublicKey(credentials.PrivateKey));
        Assert.Equal("/guito-api/eb-staging-pk", fixture.Client.Request!.Name);
        Assert.True(fixture.Client.Request.WithDecryption);
        Assert.Equal(cancellation.Token, fixture.Client.CancellationToken);
    }
}
