using GuitoApi.Configuration;
using GuitoApi.Infrastructure.EnableBanking;

namespace GuitoApi.Tests;

public class EnableBankingCredentialsProviderTests
{
    [Fact]
    public async Task GetAsync_ShouldReturnPayloadCredentials_WhenPayloadCarriesEnableBankingAsync()
    {
        // Arrange
        var provider = new PayloadEnableBankingCredentialsProvider(new FakeSecretsProvider
        {
            Payload = { EnableBanking = new EnableBankingSecrets { ApplicationId = "app-1", PrivateKey = "key-1" } },
        });

        // Act
        var credentials = await provider.GetAsync();

        // Assert
        Assert.Equal("app-1", credentials.ApplicationId);
        Assert.Equal("key-1", credentials.PrivateKey);
    }

}
