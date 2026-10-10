using GuitoApi.Infrastructure.EnableBanking;
using GuitoApi.Infrastructure.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GuitoApi.Tests;

public class SsmConfigurationSelectionTests
{
    [Theory]
    [InlineData("Local", typeof(FileSecretsProvider), typeof(FileHumanAuthSecretProvider))]
    public void ConfigureServices_ShouldSelectFileProviders_WhenLocationIsLocal(string location, Type runtimeType, Type humanType)
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
    public void ConfigureServices_ShouldSelectPayloadBankProvider_WhenSourceIsPayload(string source, Type expectedType)
    {
        // Arrange
        using var services = CreateServices("Local", source);
        using var scope = services.CreateScope();

        // Act
        var credentials = scope.ServiceProvider.GetRequiredService<IEnableBankingCredentialsProvider>();

        // Assert
        Assert.IsType(expectedType, credentials);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigureServices_ShouldRejectRetiredAwsLocation_WhenResolvingSecrets(bool humanAuth)
    {
        // Arrange
        using var services = CreateServices("Aws", "Payload");

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            humanAuth ? (object)services.GetRequiredService<IHumanAuthSecretProvider>()
                : services.GetRequiredService<ISecretsProvider>());

        // Assert
        Assert.Contains("Aws", exception.Message);
    }

    [Fact]
    public void ConfigureServices_ShouldRejectRetiredBankSource_WhenSourceIsSecretsManager()
    {
        // Arrange
        using var services = CreateServices("Local", "SecretsManager");
        using var scope = services.CreateScope();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IEnableBankingCredentialsProvider>());

        // Assert
        Assert.Contains("SecretsManager", exception.Message);
    }

    [Fact]
    public async Task GetAsync_ShouldUseSsmHumanAuthDefault_WhenParameterNameIsNotOverriddenAsync()
    {
        // Arrange
        using var client = new SsmFakeClient { Value = "{\"ClientSecret\":\"hermetic\"}" };
        using var services = CreateServices("AwsSsm", "Payload", client);

        // Act
        await services.GetRequiredService<IHumanAuthSecretProvider>().GetAsync();

        // Assert
        Assert.Equal("/guito-api/human-auth", client.Request!.Name);
    }

    private static ServiceProvider CreateServices(string location, string source, SsmFakeClient? client = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppConfiguration:Secrets:Location"] = location,
            ["AppConfiguration:Secrets:SecretName"] = "hermetic-secret",
            ["AppConfiguration:EnableBanking:SecretsSource"] = source
        }).Build();
        var services = new ServiceCollection();
        new GuitoApi.Startup(configuration).ConfigureServices(services);
        if (client is not null) services.AddSingleton<Amazon.SimpleSystemsManagement.IAmazonSimpleSystemsManagement>(client);
        return services.BuildServiceProvider();
    }
}
