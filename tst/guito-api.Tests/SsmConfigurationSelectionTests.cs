using GuitoApi.Infrastructure.EnableBanking;
using GuitoApi.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

public class SsmConfigurationSelectionTests
{
    [Theory]
    [InlineData("Aws", typeof(AwsSecretsProvider), typeof(AwsHumanAuthSecretProvider))]
    [InlineData("Local", typeof(FileSecretsProvider), typeof(FileHumanAuthSecretProvider))]
    public void ConfigureServices_ShouldRetainLegacyProviders_WhenLocationIsNotAwsSsm(string location, Type runtimeType, Type humanType)
    {
        // Arrange
        using var services = CreateServices(location, "Payload");

        // Act
        var runtime = services.GetRequiredService<ISecretsProvider>();
        var human = services.GetRequiredService<IHumanAuthSecretProvider>();

        // Assert
        Assert.IsType(runtimeType, runtime);
        Assert.IsType(humanType, human);
    }

    [Theory]
    [InlineData("Payload", typeof(PayloadEnableBankingCredentialsProvider))]
    [InlineData("SecretsManager", typeof(SecretsManagerEnableBankingCredentialsProvider))]
    public void ConfigureServices_ShouldRetainLegacyBankProviders_WhenSourceIsNotAwsSsm(string source, Type expectedType)
    {
        // Arrange
        using var services = CreateServices("Local", source);
        using var scope = services.CreateScope();

        // Act
        var credentials = scope.ServiceProvider.GetRequiredService<IEnableBankingCredentialsProvider>();

        // Assert
        Assert.IsType(expectedType, credentials);
    }

    private static ServiceProvider CreateServices(string location, string source)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppConfiguration:Secrets:Location"] = location,
            ["AppConfiguration:Secrets:SecretName"] = "hermetic-secret",
            ["AppConfiguration:EnableBanking:SecretsSource"] = source
        }).Build();
        var services = new ServiceCollection();
        new GuitoApi.Startup(configuration).ConfigureServices(services);
        return services.BuildServiceProvider();
    }
}
