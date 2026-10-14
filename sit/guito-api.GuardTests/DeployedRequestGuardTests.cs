using System.Net;
using System.Text.Json;
using GuitoApi.IntegrationTests.Common;
using Xunit;

namespace GuitoApi.GuardTests;

// Separate hermetic project/process: environment changes cannot affect deployed suites.
public class DeployedRequestGuardTests
{
    [Theory]
    [InlineData("POST", "/Expense")]
    [InlineData("DELETE", "/Expense/7")]
    [InlineData("PUT", "/Expense/7")]
    [InlineData("POST", "/Auth/logout")]
    [InlineData("POST", "/Auth/token")]
    public async Task SendAsync_ShouldBlockBeforeTransport_WhenProductionMutationIsAttempted(string method, string route)
    {
        Configure("production", "round-trip");
        using var transport = new RecordingTransport();
        using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new HttpRequestMessage(new HttpMethod(method), route)));

        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task SendAsync_ShouldPermitReadOnlyMethods_WhenProductionIsSelected(string method)
    {
        Configure("production", "read-only");
        using var transport = new RecordingTransport();
        using var client = CreateClient(transport);

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/Category"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, transport.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("expired")]
    [InlineData("other-origin")]
    [InlineData("personal-data")]
    [InlineData("read-only")]
    public async Task SendAsync_ShouldBlockBeforeTransport_WhenIsolationIsNotEstablished(string? failure)
    {
        Configure("staging", failure == "read-only" ? "read-only" : "round-trip");
        if (failure != null) SetAttestation(failure);
        using var transport = new RecordingTransport();
        using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("/Expense", null));

        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task SendAsync_ShouldPermitMutation_WhenDisposableStagingIsAttested()
    {
        Configure("staging", "round-trip");
        SetAttestation("");
        using var transport = new RecordingTransport();
        using var client = CreateClient(transport);

        using var response = await client.PostAsync("/Expense", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task SendAsync_ShouldBlockBeforeTransport_WhenOriginChanges()
    {
        Configure("production", "read-only");
        using var transport = new RecordingTransport();
        using var client = CreateClient(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://other.example/Expense"));

        Assert.Equal(0, transport.Calls);
    }

    private static HttpClient CreateClient(HttpMessageHandler transport) =>
        new(new DeployedRequestGuard(transport)) { BaseAddress = new Uri("https://staging.example") };

    private static void Configure(string environment, string mode)
    {
        Environment.SetEnvironmentVariable("GUITO_TARGET_BASE_URL", "https://staging.example");
        Environment.SetEnvironmentVariable("GUITO_TARGET_ENVIRONMENT", environment);
        Environment.SetEnvironmentVariable("GUITO_TEST_MODE", mode);
        Environment.SetEnvironmentVariable("GUITO_STAGING_ISOLATION", null);
    }

    private static void SetAttestation(string failure) =>
        Environment.SetEnvironmentVariable("GUITO_STAGING_ISOLATION", JsonSerializer.Serialize(new
        {
            environment = "staging", baseUrl = failure == "other-origin" ? "https://other.example" : "https://staging.example",
            disposable = true, noPersonalData = failure != "personal-data", exclusiveDatabaseCredentials = true,
            syntheticCreateDeleteApproved = true, category = "Existing category",
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(failure == "expired" ? -1 : 30)
        }));

    private sealed class RecordingTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
