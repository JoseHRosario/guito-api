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
        using var fixture = new ProviderFixture(kind);
        await fixture.WarmAsync(kind);
        using var secondScope = fixture.CreateScope();

        // Act
        fixture.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        await GetAsync(secondScope.ServiceProvider, kind);
        var cachedCalls = fixture.Client.Calls;
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        await GetAsync(secondScope.ServiceProvider, kind);

        // Assert
        Assert.Equal(1, cachedCalls);
        Assert.Equal(2, fixture.Client.Calls);
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("human")]
    [InlineData("bank")]
    public async Task GetAsync_ShouldHonorCancellation_WhenValueIsCachedAsync(string kind)
    {
        // Arrange
        using var fixture = new ProviderFixture(kind);
        await fixture.WarmAsync(kind);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var exception = await Record.ExceptionAsync(() => GetAsync(fixture.Services, kind, cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(1, fixture.Client.Calls);
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
        using var fixture = new ProviderFixture(kind);
        await fixture.WarmAsync(kind);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Client.Failure = new InvalidOperationException("hermetic upstream failure");

        // Act
        var exception = await Record.ExceptionAsync(() => GetAsync(fixture.Services, kind));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(2, fixture.Client.Calls);
    }

    private sealed class ProviderFixture : IDisposable
    {
        public SsmFakeClient Client { get; }
        public SsmFakeTimeProvider Clock { get; } = new();
        private readonly ServiceProvider _services;
        private readonly IServiceScope _scope;
        public IServiceProvider Services => _scope.ServiceProvider;

        public ProviderFixture(string kind, string? payload = null)
        {
            Client = new SsmFakeClient { Value = payload ?? Payload(kind) };
            _services = CreateServices(Client, Clock);
            _scope = _services.CreateScope();
        }

        public async Task WarmAsync(string kind) => await GetAsync(Services, kind);
        public IServiceScope CreateScope() => _services.CreateScope();

        public void Dispose()
        {
            _scope.Dispose();
            _services.Dispose();
            Client.Dispose();
        }
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
