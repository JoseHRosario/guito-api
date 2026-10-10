using GuitoApi.Infrastructure.EnableBanking;
using GuitoApi.Infrastructure.Secrets;
using Microsoft.Extensions.DependencyInjection;
using static GuitoApi.Tests.SsmSecretsProviderTests;

namespace GuitoApi.Tests;

public class SsmProviderContractTests
{
    [Theory]
    [InlineData("runtime")]
    [InlineData("human")]
    [InlineData("bank")]
    public async Task GetAsync_ShouldRefreshAtFiveMinutesAcrossScopes_WhenValueIsCachedAsync(string kind)
    {
        // Arrange
        using var client = new SsmFakeClient { Value = Payload(kind) };
        var clock = new SsmFakeTimeProvider();
        using var services = CreateServices(client, clock);
        using var firstScope = services.CreateScope();
        await GetAsync(firstScope.ServiceProvider, kind);
        using var secondScope = services.CreateScope();

        // Act
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        await GetAsync(secondScope.ServiceProvider, kind);
        var cachedCalls = client.Calls;
        clock.Advance(TimeSpan.FromTicks(1));
        await GetAsync(secondScope.ServiceProvider, kind);

        // Assert
        Assert.Equal(1, cachedCalls);
        Assert.Equal(2, client.Calls);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("human")]
    [InlineData("bank")]
    public async Task GetAsync_ShouldHonorCancellation_WhenValueIsCachedAsync(string kind)
    {
        // Arrange
        using var client = new SsmFakeClient { Value = Payload(kind) };
        using var services = CreateServices(client);
        using var scope = services.CreateScope();
        await GetAsync(scope.ServiceProvider, kind);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var exception = await Record.ExceptionAsync(() => GetAsync(scope.ServiceProvider, kind, cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("human")]
    [InlineData("bank")]
    public async Task GetAsync_ShouldRetryAfterFailure_WhenParameterReadFailsAsync(string kind)
    {
        // Arrange
        using var client = new SsmFakeClient { Value = Payload(kind), Failure = new InvalidOperationException("hermetic upstream failure") };
        using var services = CreateServices(client);
        using var scope = services.CreateScope();

        // Act
        var exception = await Record.ExceptionAsync(() => GetAsync(scope.ServiceProvider, kind));
        client.Failure = null;
        await GetAsync(scope.ServiceProvider, kind);

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(2, client.Calls);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("human")]
    [InlineData("bank")]
    public async Task GetAsync_ShouldNotCacheInvalidPayload_WhenParameterValueIsInvalidAsync(string kind)
    {
        // Arrange
        using var client = new SsmFakeClient { Value = "null" };
        using var services = CreateServices(client);
        using var scope = services.CreateScope();

        // Act
        var exception = await Record.ExceptionAsync(() => GetAsync(scope.ServiceProvider, kind));
        client.Value = Payload(kind);
        await GetAsync(scope.ServiceProvider, kind);

        // Assert
        Assert.NotNull(exception);
        Assert.Equal(2, client.Calls);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("human")]
    [InlineData("bank")]
    public async Task GetAsync_ShouldRejectExpiredCache_WhenRefreshFailsAsync(string kind)
    {
        // Arrange
        using var client = new SsmFakeClient { Value = Payload(kind) };
        var clock = new SsmFakeTimeProvider();
        using var services = CreateServices(client, clock);
        using var scope = services.CreateScope();
        await GetAsync(scope.ServiceProvider, kind);
        clock.Advance(TimeSpan.FromMinutes(5));
        client.Failure = new InvalidOperationException("hermetic upstream failure");

        // Act
        var exception = await Record.ExceptionAsync(() => GetAsync(scope.ServiceProvider, kind));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(2, client.Calls);
    }

    private static Task GetAsync(IServiceProvider services, string kind, CancellationToken cancellationToken = default) => kind switch
    {
        "runtime" => services.GetRequiredService<ISecretsProvider>().GetAsync(cancellationToken),
        "human" => services.GetRequiredService<IHumanAuthSecretProvider>().GetAsync(cancellationToken),
        _ => services.GetRequiredService<IEnableBankingCredentialsProvider>().GetAsync(cancellationToken)
    };

    private static string Payload(string kind)
    {
        if (kind != "bank") return "{}";
        using var key = System.Security.Cryptography.RSA.Create(2048);
        return key.ExportPkcs8PrivateKeyPem();
    }
}
