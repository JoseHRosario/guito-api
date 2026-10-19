using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.RDSDataService.Model;
using static GuitoApi.Tests.UserSessionBoundaryTests;

namespace GuitoApi.Tests;

public class UserSessionColdStartTests
{
    [Theory]
    [InlineData("sql")]
    [InlineData("credentials")]
    [InlineData("network")]
    public async Task Establish_ShouldNotRetryOrClaimStarting_WhenReadinessFailureIsNotKnownStartup(string failure)
    {
        using var scenario = ArrangeUnknownFailure(failure);

        var failed = await Login(scenario.Client);

        await AssertUnknownReadinessFailureAsync(scenario.State, failed);
    }

    [Fact]
    public async Task Establish_ShouldNotReplayMutationOrCode_WhenDatabaseFailsAfterReadiness()
    {
        using var scenario = ArrangeMutationFailure();

        var failed = await Login(scenario.Client);

        await AssertMutationFailureAsync(scenario.State, failed);
    }

    [Theory]
    [InlineData("database")]
    [InlineData("provider")]
    [InlineData("configuration")]
    [InlineData("google-configuration")]
    public async Task Establish_ShouldLogOnlyCategoricalStageAndType_WhenSensitiveFailureOccurs(string failure)
    {
        using var scenario = new Scenario();
        var signedToken = ArrangeSensitiveFailure(scenario.State, failure);

        var failed = await Login(scenario.Client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        AssertCategoricalLogs(scenario.State, failure, signedToken);
        Assert.Equal("temporarily_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Refresh_ShouldBoundAcquireAndPreserveSession_WhenLeaseTransportStalls()
    {
        using var scenario = await ArrangeLeaseStallAsync();

        var pending = Send(scenario.Client, "refresh", scenario.Cookie);
        await scenario.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AdvanceOnTimerAsync(scenario.State, 25);
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        await AssertAcquireTimeoutAsync(scenario.State, failed);
        scenario.State.BeforeStatement = null;
        Assert.Contains("stage=lease_acquire category=request_timeout type=TaskCanceledException", string.Join("\n", scenario.State.Logs));

        var recovered = await Send(scenario.Client, "refresh", scenario.Cookie);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task Establish_ShouldWaitForSharedTransportResumeDelays_WhenDatabaseRecovers()
    {
        using var scenario = new Scenario();
        var attempts = ArrangeResumingDatabase(scenario.State);

        var pending = Login(scenario.Client);
        await scenario.State.Clock.WaitForTimerAsync(1);
        Assert.Empty(scenario.State.Forms);
        scenario.State.Clock.Advance(TimeSpan.FromSeconds(1));
        await scenario.State.Clock.WaitForTimerAsync(5);
        Assert.Empty(scenario.State.Forms);
        scenario.State.Clock.Advance(TimeSpan.FromSeconds(5));
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        AssertResumedLogin(scenario.State, response, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Establish_ShouldReturnStartingWithoutConsumingCode_WhenKnownStartupExhaustsReadinessBudget(bool resuming)
    {
        using var scenario = ArrangeKnownStartupFailure(resuming);

        var pending = Login(scenario.Client);
        await scenario.State.Clock.WaitForTimerAsync(18);
        await scenario.State.Clock.WaitForTimerAsync(1);
        await AdvanceReadinessBudgetAsync(scenario.State, resuming);
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        await AssertReadinessExhaustedAsync(scenario.State, failed, resuming);
        scenario.State.BeforeStatement = null;

        var recovered = await Login(scenario.Client);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Single(scenario.State.Forms);
    }

    [Fact]
    public async Task Establish_ShouldNotConsumeCode_WhenDatabaseReadinessFails()
    {
        using var scenario = new Scenario(false);
        scenario.State.Database.Fail = true;

        var failed = await Login(scenario.Client);

        await AssertReadinessFailureAsync(scenario.State, failed);
        scenario.State.Database.Fail = false;

        var recovered = await Login(scenario.Client);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Single(scenario.State.Forms);
    }

    private static void ArrangeUnknownReadinessFailure(UserSessionTestHost.State state, string failure)
    {
        state.BeforeStatement = (_, _) => throw (failure switch
        {
            "sql" => new BadRequestException("fake-sensitive-db-secret"),
            "credentials" => new Amazon.RDSDataService.AmazonRDSDataServiceException("fake-sensitive-db-secret") { ErrorCode = "AccessDeniedException" },
            _ => new HttpRequestException("fake-sensitive-db-secret")
        });
    }

    private static string ArrangeSensitiveFailure(UserSessionTestHost.State state, string failure)
    {
        if (failure == "database") state.BeforeStatement = (_, _) => throw new InvalidOperationException("fake-sensitive-db-secret");
        if (failure == "provider") state.NetworkFailure = true;
        if (failure == "configuration") state.Key = "fake-sensitive-invalid-key";
        if (failure == "google-configuration") state.InvalidGoogleConfiguration = true;
        var signedToken = state.Token();
        state.Body = JsonSerializer.Serialize(new { id_token = signedToken, access_token = "fake-access-token",
            expires_in = 3600, refresh_token = "fake-refresh-credential" });
        return signedToken;
    }

    private static async Task AssertUnknownReadinessFailureAsync(UserSessionTestHost.State state, HttpResponseMessage failed)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("temporarily_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal("SELECT 1", Assert.Single(state.Statements));
        Assert.Empty(state.Forms);
        Assert.Empty(state.Database.CredentialsWritten);
        Assert.DoesNotContain("fake-sensitive-db-secret", string.Join("\n", state.Logs));
        Assert.Empty(state.LoggedExceptions);
    }

    private static void AssertCategoricalLogs(UserSessionTestHost.State state, string failure, string signedToken)
    {
        var logs = string.Join("\n", state.Logs);
        Assert.Contains("stage=", logs);
        Assert.Contains(failure switch { "database" => "datastore_unavailable", "provider" => "provider_unavailable", _ => "configuration_unavailable" }, logs);
        Assert.Contains("type=", logs);
        AssertNoSensitiveLogs(state, signedToken, logs);
        Assert.Empty(state.LoggedExceptions);
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
        state.BeforeStatement = async (request, token) =>
        {
            if (!request.Sql.Contains("RETURNING subject")) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        return entered;
    }

    private static Func<int> ArrangeResumingDatabase(UserSessionTestHost.State state)
    {
        var attempts = 0;
        state.BeforeStatement = (request, _) =>
        {
            if (request.Sql == "SELECT 1" && ++attempts <= 2) throw new DatabaseResumingException("fake-sensitive-db-secret");
            return Task.CompletedTask;
        };
        return () => attempts;
    }

    private static async Task AssertAcquireTimeoutAsync(UserSessionTestHost.State state, HttpResponseMessage failed)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("temporarily_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.Single(state.Forms);
        Assert.Single(state.Database.CredentialsWritten);
    }

    private static void AssertResumedLogin(UserSessionTestHost.State state, HttpResponseMessage response, Func<int> attempts)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts());
        Assert.Single(state.Forms);
        Assert.Single(state.Database.CredentialsWritten);
        Assert.Equal("SELECT 1", state.Statements[0]);
        Assert.Contains("category=database_resuming type=DatabaseResumingException", string.Join("\n", state.Logs));
    }

    private static async Task AssertReadinessExhaustedAsync(UserSessionTestHost.State state, HttpResponseMessage failed, bool resuming)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("service_starting", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Contains("try again", await failed.Content.ReadAsStringAsync());
        if (!resuming) Assert.Contains("category=database_unavailable type=DatabaseUnavailableException", string.Join("\n", state.Logs));
        Assert.Contains("stage=database_readiness category=readiness_timeout", string.Join("\n", state.Logs));
        Assert.DoesNotContain("fake-sensitive-db-secret", string.Join("\n", state.Logs));
        Assert.Empty(state.LoggedExceptions);
        Assert.Empty(state.Forms);
        Assert.Empty(state.Database.CredentialsWritten);
        Assert.False(failed.Headers.Contains("Set-Cookie"));
    }

    private static async Task AdvanceReadinessBudgetAsync(UserSessionTestHost.State state, bool resuming)
    {
        if (resuming)
        {
            state.Clock.Advance(TimeSpan.FromSeconds(1));
            await state.Clock.WaitForTimerAsync(5);
            state.Clock.Advance(TimeSpan.FromSeconds(5));
            await state.Clock.WaitForTimerAsync(1);
        }
        state.Clock.Advance(TimeSpan.FromSeconds(resuming ? 12 : 18));
    }

    private static Scenario ArrangeUnknownFailure(string failure)
    {
        var scenario = new Scenario();
        ArrangeUnknownReadinessFailure(scenario.State, failure);
        return scenario;
    }

    private static Scenario ArrangeMutationFailure()
    {
        var scenario = new Scenario();
        scenario.State.BeforeStatement = (request, _) => request.Sql.StartsWith("INSERT")
            ? throw new DatabaseUnavailableException("fake-sensitive-db-secret") : Task.CompletedTask;
        return scenario;
    }

    private static Scenario ArrangeKnownStartupFailure(bool resuming)
    {
        var scenario = new Scenario();
        scenario.State.BeforeStatement = (_, _) => throw (resuming
            ? new DatabaseResumingException("fake-sensitive-db-secret") : new DatabaseUnavailableException("fake-sensitive-db-secret"));
        return scenario;
    }

    private static async Task<Scenario> ArrangeLeaseStallAsync()
    {
        var scenario = new Scenario();
        scenario.Cookie = Cookie(await Login(scenario.Client));
        scenario.Entered = ArrangeStalledLease(scenario.State);
        return scenario;
    }

    private static async Task AdvanceOnTimerAsync(UserSessionTestHost.State state, int seconds)
    {
        await state.Clock.WaitForTimerAsync(seconds);
        state.Clock.Advance(TimeSpan.FromSeconds(seconds));
    }

    private static async Task AssertMutationFailureAsync(UserSessionTestHost.State state, HttpResponseMessage failed)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("temporarily_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Single(state.Statements, sql => sql.StartsWith("INSERT"));
        Assert.Single(state.Forms);
        Assert.Empty(state.Database.CredentialsWritten);
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("fake-sensitive-db-secret", string.Join("\n", state.Logs));
        Assert.Empty(state.LoggedExceptions);
    }

    private static async Task AssertReadinessFailureAsync(UserSessionTestHost.State state, HttpResponseMessage failed)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("temporarily_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Empty(state.Forms);
        Assert.Empty(state.Database.CredentialsWritten);
        Assert.False(failed.Headers.Contains("Set-Cookie"));
    }

    private sealed class Scenario : IDisposable
    {
        public UserSessionTestHost.State State { get; }
        public UserSessionTestHost Host { get; }
        private HttpClient? _client;
        public HttpClient Client => _client ??= UserSessionBoundaryTests.Client(Host);
        public string Cookie { get; set; } = "";
        public TaskCompletionSource Entered { get; set; } = null!;

        public Scenario(bool sharedTransport = true)
        {
            State = new() { SharedTransport = sharedTransport };
            Host = new(State);
        }

        public void Dispose()
        {
            _client?.Dispose();
            Host.Dispose();
        }
    }
}
