using GuitoApi.Configuration;
using GuitoApi.Infrastructure.EnableBanking;
using Microsoft.Extensions.Options;

namespace GuitoApi.Tests;

/// <summary>
/// EB credentials providers (issue #89): the ADR-0008 payload source and the dedicated
/// Secrets Manager source ({"pem": …}, flattened-PEM tolerant).
/// </summary>
public class EnableBankingCredentialsProviderTests
{
    private const string FlatPem = "-----BEGIN PRIVATE KEY----- " + PemBody + " -----END PRIVATE KEY-----";
    private const string PemBody = "MIIJQwIBADANBgkqhkiG9w0BAQEFAASC";

    private static EnableBankingOptions SecretsManagerOptions() => new()
    {
        ApplicationId = "4d940de4-0656-4dd1-92fd-48fa58e5a6c8",
        SecretsSource = "SecretsManager",
        SecretsManagerSecretName = "guito-api/eb-staging-pk",
    };

    [Fact]
    public async Task PayloadProvider_ShouldReturnPayloadCredentials_WhenPayloadCarriesEnableBanking()
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

    [Fact]
    public async Task SecretsManagerProvider_ShouldReadPemAndConfigApplicationId_WhenSecretIsJson()
    {
        // Arrange
        string? requestedSecretId = null;
        var provider = new SecretsManagerEnableBankingCredentialsProvider(
            Microsoft.Extensions.Options.Options.Create(SecretsManagerOptions()),
            (secretId, _) =>
            {
                requestedSecretId = secretId;
                return Task.FromResult($$"""{"pem": "{{FlatPem}}"}""");
            });

        // Act
        var credentials = await provider.GetAsync();

        // Assert
        Assert.Equal("guito-api/eb-staging-pk", requestedSecretId);
        Assert.Equal(SecretsManagerOptions().ApplicationId, credentials.ApplicationId);
        Assert.Equal("-----BEGIN PRIVATE KEY-----", credentials.PrivateKey.Split('\n')[0]);
        Assert.StartsWith(PemBody, credentials.PrivateKey.Split('\n')[1]);
        Assert.EndsWith("-----END PRIVATE KEY-----", credentials.PrivateKey.TrimEnd());
    }

    [Fact]
    public async Task SecretsManagerProvider_ShouldThrow_WhenApplicationIdMissing()
    {
        // Arrange
        var options = SecretsManagerOptions();
        options.ApplicationId = string.Empty;
        var provider = new SecretsManagerEnableBankingCredentialsProvider(
            Microsoft.Extensions.Options.Options.Create(options), (_, _) => Task.FromResult($$"""{"pem": "x"}"""));

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync());
    }

    [Fact]
    public void NormalizePem_ShouldWrapBase64At64Characters_WhenPemArrivesFlattened()
    {
        // Arrange / Act
        var normalized = SecretsManagerEnableBankingCredentialsProvider.NormalizePem(FlatPem);

        // Assert — every line except BEGIN/END is ≤64 chars of pure base64.
        var lines = normalized.TrimEnd().Split('\n', StringSplitOptions.TrimEntries);
        Assert.Equal("-----BEGIN PRIVATE KEY-----", lines[0]);
        Assert.Equal("-----END PRIVATE KEY-----", lines[^1]);
        Assert.All(lines[1..^1], line => Assert.True(line.Length <= 64));
        Assert.Equal(PemBody, string.Concat(lines[1..^1]));
    }
}