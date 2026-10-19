using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using static GuitoApi.Tests.UserSessionBoundaryTests;

namespace GuitoApi.Tests;

public class UserSessionColdStartTests
{
    [Theory]
    [InlineData("throttled")]
    [InlineData("credentials")]
    [InlineData("missing-table")]
    [InlineData("network")]
    public async Task Establish_ShouldNotRetryOrConsumeCode_WhenReadinessFails(string failure)
    {
        // Arrange
        using var scenario = new Scenario();
        scenario.State.BeforeDynamoRequest = (_, _) => throw StorageFailure(failure);

        // Act
        var failed = await Login(scenario.Client);

        // Assert
        await AssertTemporaryAsync(scenario.State, failed);
        Assert.IsType<GetItemRequest>(Assert.Single(scenario.State.DynamoRequests));
        Assert.Empty(scenario.State.Forms);
        Assert.Empty(scenario.State.Database.CredentialsWritten);
    }

    [Fact]
    public async Task Establish_ShouldNotReplayMutationOrCode_WhenPersistenceFailsAfterReadiness()
    {
        // Arrange
        using var scenario = new Scenario();
        scenario.State.BeforeDynamoRequest = (request, _) => request is PutItemRequest
            ? throw StorageFailure("network") : Task.CompletedTask;

        // Act
        var failed = await Login(scenario.Client);

        // Assert
        await AssertTemporaryAsync(scenario.State, failed);
        Assert.Single(scenario.State.DynamoRequests.OfType<PutItemRequest>());
        Assert.Single(scenario.State.Forms);
        Assert.Empty(scenario.State.Database.CredentialsWritten);
    }

    [Theory]
    [InlineData("database")]
    [InlineData("provider")]
    [InlineData("configuration")]
    [InlineData("google-configuration")]
    public async Task Establish_ShouldLogOnlyCategoricalStageAndType_WhenSensitiveFailureOccurs(string failure)
    {
        // Arrange
        using var scenario = new Scenario();
        var signedToken = ArrangeSensitiveFailure(scenario.State, failure);

        // Act
        var failed = await Login(scenario.Client);

        // Assert
        await AssertTemporaryAsync(scenario.State, failed);
        AssertCategoricalLogs(scenario.State, failure, signedToken);
    }

    [Fact]
    public async Task Refresh_ShouldBoundAcquireAndPreserveSession_WhenLeaseTransportStalls()
    {
        // Arrange
        using var scenario = new Scenario();
        var cookie = Cookie(await Login(scenario.Client));
        var entered = ArrangeStalledLease(scenario.State);

        // Act
        var pending = Send(scenario.Client, "refresh", cookie);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        scenario.State.Clock.Advance(TimeSpan.FromSeconds(25));
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await AssertTemporaryAsync(scenario.State, failed);
        scenario.State.BeforeDynamoRequest = null;
        Assert.Equal(HttpStatusCode.OK, (await Send(scenario.Client, "refresh", cookie)).StatusCode);
    }

    [Fact]
    public async Task Establish_ShouldNotConsumeCode_WhenSessionStorageIsUnavailable()
    {
        // Arrange
        using var scenario = new Scenario();
        scenario.State.Database.Fail = true;

        // Act
        var failed = await Login(scenario.Client);

        // Assert
        await AssertTemporaryAsync(scenario.State, failed);
        Assert.Empty(scenario.State.Forms);
        scenario.State.Database.Fail = false;
        Assert.Equal(HttpStatusCode.OK, (await Login(scenario.Client)).StatusCode);
        Assert.Single(scenario.State.Forms);
    }

    private static Exception StorageFailure(string failure) => failure switch
    {
        "throttled" => new ProvisionedThroughputExceededException("fake-sensitive-db-secret"),
        "credentials" => new AmazonDynamoDBException("fake-sensitive-db-secret") { ErrorCode = "AccessDeniedException" },
        "missing-table" => new ResourceNotFoundException("fake-sensitive-db-secret"),
        _ => new HttpRequestException("fake-sensitive-db-secret")
    };

    private static string ArrangeSensitiveFailure(UserSessionTestHost.State state, string failure)
    {
        if (failure == "database") state.BeforeDynamoRequest = (_, _) => throw StorageFailure("credentials");
        if (failure == "provider") state.NetworkFailure = true;
        if (failure == "configuration") state.Key = "fake-sensitive-invalid-key";
        if (failure == "google-configuration") state.InvalidGoogleConfiguration = true;
        var signedToken = state.Token();
        state.Body = JsonSerializer.Serialize(new { id_token = signedToken, access_token = "fake-access-token",
            expires_in = 3600, refresh_token = "fake-refresh-credential" });
        return signedToken;
    }

    private static async Task AssertTemporaryAsync(UserSessionTestHost.State state, HttpResponseMessage failed)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("temporarily_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("fake-sensitive", await failed.Content.ReadAsStringAsync());
        Assert.DoesNotContain("fake-sensitive", string.Join("\n", state.Logs));
        Assert.Empty(state.LoggedExceptions);
    }

    private static void AssertCategoricalLogs(UserSessionTestHost.State state, string failure, string signedToken)
    {
        var logs = string.Join("\n", state.Logs);
        Assert.Contains("stage=", logs);
        Assert.Contains(failure switch { "database" => "datastore_unavailable", "provider" => "provider_unavailable", _ => "configuration_unavailable" }, logs);
        Assert.Contains("type=", logs);
        AssertNoSensitiveLogs(state, signedToken, logs);
    }

    private static void AssertNoSensitiveLogs(UserSessionTestHost.State state, string signedToken, string logs)
    {
        Assert.DoesNotContain("fake-sensitive", logs);
        Assert.DoesNotContain("fake-refresh-credential", logs);
        Assert.DoesNotContain("fake-code", logs);
        Assert.DoesNotContain("fake-access-token", logs);
        Assert.DoesNotContain("fake-client-secret", logs);
        Assert.DoesNotContain(new string('a', 43), logs);
        Assert.DoesNotContain(signedToken, logs);
        Assert.DoesNotContain(UserSessionTestHost.Email, logs);
        Assert.DoesNotContain(state.Key, logs);
    }

    private static TaskCompletionSource ArrangeStalledLease(UserSessionTestHost.State state)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.BeforeDynamoRequest = async (request, token) =>
        {
            if (request is not UpdateItemRequest { ReturnValues.Value: "ALL_NEW" }) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        return entered;
    }

    private sealed class Scenario : IDisposable
    {
        public UserSessionTestHost.State State { get; } = new();
        public UserSessionTestHost Host { get; }
        private HttpClient? _client;
        public HttpClient Client => _client ??= UserSessionBoundaryTests.Client(Host);
        public Scenario() => Host = new(State);
        public void Dispose() { _client?.Dispose(); Host.Dispose(); }
    }
}
