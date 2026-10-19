using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.RDSDataService.Model;
using static GuitoApi.Tests.UserSessionBoundaryTests;

namespace GuitoApi.Tests;

public class UserSessionDeadlineTests
{
    [Theory]
    [InlineData("readiness")]
    [InlineData("acquire")]
    [InlineData("provider")]
    [InlineData("completion")]
    public async Task Refresh_ShouldPreserveSessionAndReleaseLease_WhenCallerCancels(string stage)
    {
        using var scenario = await ArrangeRefreshStageAsync(stage, ArrangeCancellationStage);
        using var cancelled = new CancellationTokenSource();

        var pending = scenario.Host.Server.SendAsync(context => ArrangeRefreshRequest(context, scenario.Cookie, cancelled.Token));
        await scenario.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.Cancel();
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        AssertCallerCancelled(scenario.State, failed);
        ArrangeRecovery(scenario.State);

        var recovered = await Send(scenario.Client, "refresh", scenario.Cookie);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("completion")]
    public async Task Refresh_ShouldShareOneBudgetAndPreserveSession_WhenLaterStageStalls(string stage)
    {
        using var scenario = await ArrangeRefreshStageAsync(stage, ArrangeLaterStageStall);

        var pending = Send(scenario.Client, "refresh", scenario.Cookie);
        await AssertReadinessRetryPendingAsync(scenario.State);
        scenario.State.Clock.Advance(TimeSpan.FromSeconds(1));
        await scenario.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await AdvanceToLastBudgetSecondAsync(scenario.State, pending);
        scenario.State.Clock.Advance(TimeSpan.FromSeconds(1));
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        await AssertSharedBudgetExpiredAsync(scenario.State, failed, stage);
        ArrangeRecovery(scenario.State);

        var recovered = await Send(scenario.Client, "refresh", scenario.Cookie);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldAwaitAtMostTwoSecondsOfCleanup_WhenBudgetExpiresDuringProviderCall()
    {
        using var scenario = await ArrangeCleanupStallAsync();

        var pending = Send(scenario.Client, "refresh", scenario.Cookie);
        await AdvanceToCleanupAsync(scenario);
        await AssertCleanupPendingAsync(scenario.State, pending);
        scenario.State.Clock.Advance(TimeSpan.FromSeconds(2));
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        await AssertCleanupBoundAsync(scenario.State, failed);
        ArrangeRecovery(scenario.State);

        var occupied = await Send(scenario.Client, "refresh", scenario.Cookie);
        await AssertTemporaryAsync(occupied);
        scenario.State.Clock.Now = scenario.State.Clock.Now.AddMinutes(2);
        var recovered = await Send(scenario.Client, "refresh", scenario.Cookie);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldPreserveCookieAndCredential_WhenReadinessStartupBudgetExhausts()
    {
        using var scenario = await ArrangeReadinessFailureAsync();

        var pending = Send(scenario.Client, "refresh", scenario.Cookie);
        await scenario.State.Clock.WaitForTimerAsync(1);
        scenario.State.Clock.Advance(TimeSpan.FromSeconds(18));
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        await AssertRefreshReadinessExhaustedAsync(scenario.State, failed);
        scenario.State.BeforeStatement = null;

        var recovered = await Send(scenario.Client, "refresh", scenario.Cookie);

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Theory]
    [InlineData("readiness")]
    [InlineData("provider")]
    [InlineData("persistence")]
    public async Task Establish_ShouldRespectDeadlineWithoutReplayingCode_WhenStageStalls(string stage)
    {
        using var scenario = new Scenario();
        var entered = ArrangeEstablishStall(scenario.State, stage);

        var pending = Login(scenario.Client);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        scenario.State.Clock.Advance(TimeSpan.FromSeconds(stage == "readiness" ? 18 : 25));
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        await AssertEstablishDeadlineAsync(scenario.State, failed, stage);
    }

    private static async Task AssertTemporaryAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("temporarily_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static TaskCompletionSource ArrangeCancellationStage(UserSessionTestHost.State state, string stage)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.BeforeStatement = async (request, token) =>
        {
            if (!IsBlockedRefreshStatement(stage, request.Sql)) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        if (stage == "provider") ArrangeProviderStall(state, entered);
        return entered;
    }

    private static void ArrangeRefreshRequest(Microsoft.AspNetCore.Http.HttpContext context, string cookie, CancellationToken cancellationToken)
    {
        context.Request.Method = "POST";
        context.Request.Path = "/Auth/session/refresh";
        context.Request.Scheme = "https";
        context.Request.Headers["Origin"] = UserSessionTestHost.Origin;
        context.Request.Headers["X-Guito-Session"] = "1";
        context.Request.Headers["Cookie"] = cookie;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}"));
        context.RequestAborted = cancellationToken;
    }

    private static TaskCompletionSource ArrangeLaterStageStall(UserSessionTestHost.State state, string stage)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readinessAttempts = 0;
        state.BeforeStatement = async (request, token) =>
        {
            if (request.Sql == "SELECT 1" && ++readinessAttempts == 1) throw new DatabaseUnavailableException("fake-sensitive-db-secret");
            if (stage != "completion" || !request.Sql.Contains("SET encrypted_credential")) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        if (stage == "provider") ArrangeProviderStall(state, entered);
        return entered;
    }

    private static TaskCompletionSource ArrangeStalledCleanup(UserSessionTestHost.State state)
    {
        state.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.BeforeStatement = async (request, token) =>
        {
            if (!request.Sql.StartsWith("UPDATE user_sessions SET refresh_lease = NULL")) return;
            cleanup.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        return cleanup;
    }

    private static TaskCompletionSource ArrangeEstablishStall(UserSessionTestHost.State state, string stage)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.BeforeStatement = async (request, token) =>
        {
            if (!(stage == "readiness" && request.Sql == "SELECT 1") && !(stage == "persistence" && request.Sql.StartsWith("INSERT"))) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        if (stage == "provider") ArrangeProviderStall(state, entered);
        return entered;
    }

    private static void AssertCallerCancelled(UserSessionTestHost.State state, Microsoft.AspNetCore.Http.HttpContext failed)
    {
        Assert.Equal(503, failed.Response.StatusCode);
        Assert.False(failed.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Contains("category=client_cancelled", string.Join("\n", state.Logs));
        Assert.Single(state.Database.CredentialsWritten);
        Assert.Empty(state.LoggedExceptions);
    }

    private static async Task AssertSharedBudgetExpiredAsync(UserSessionTestHost.State state, HttpResponseMessage failed, string stage)
    {
        await AssertTemporaryAsync(failed);
        Assert.Single(state.Database.CredentialsWritten);
        Assert.Equal(2, state.Forms.Count);
        Assert.Contains($"stage={(stage == "provider" ? "provider_refresh" : "session_complete")} category=request_timeout", string.Join("\n", state.Logs));
        Assert.Empty(state.LoggedExceptions);
    }

    private static async Task AssertCleanupBoundAsync(UserSessionTestHost.State state, HttpResponseMessage failed)
    {
        await AssertTemporaryAsync(failed);
        Assert.Equal(TimeSpan.FromSeconds(27), TimeSpan.FromTicks(state.Clock.GetTimestamp()));
        Assert.Single(state.Database.CredentialsWritten);
        Assert.Contains("stage=lease_release", string.Join("\n", state.Logs));
        Assert.Empty(state.LoggedExceptions);
    }

    private static async Task AssertRefreshReadinessExhaustedAsync(UserSessionTestHost.State state, HttpResponseMessage failed)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("service_starting", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.Single(state.Forms);
        Assert.Single(state.Database.CredentialsWritten);
    }

    private static async Task AssertEstablishDeadlineAsync(UserSessionTestHost.State state, HttpResponseMessage failed, string stage)
    {
        await AssertTemporaryAsync(failed);
        Assert.Equal(stage == "readiness" ? 0 : 1, state.Forms.Count);
        Assert.Contains("category=request_timeout", string.Join("\n", state.Logs));
        Assert.Empty(state.Database.CredentialsWritten);
        Assert.Empty(state.LoggedExceptions);
    }

    private static bool IsBlockedRefreshStatement(string stage, string sql) => stage switch
    {
        "readiness" => sql == "SELECT 1",
        "acquire" => sql.Contains("RETURNING subject"),
        "completion" => sql.Contains("SET encrypted_credential"),
        _ => false
    };

    private static void ArrangeProviderStall(UserSessionTestHost.State state, TaskCompletionSource entered)
    {
        state.Entered = entered;
        state.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task<Scenario> ArrangeRefreshStageAsync(string stage,
        Func<UserSessionTestHost.State, string, TaskCompletionSource> arrangeStage)
    {
        var scenario = new Scenario();
        scenario.Cookie = Cookie(await Login(scenario.Client));
        scenario.Entered = arrangeStage(scenario.State, stage);
        return scenario;
    }

    private static async Task<Scenario> ArrangeCleanupStallAsync()
    {
        var scenario = new Scenario();
        scenario.Cookie = Cookie(await Login(scenario.Client));
        scenario.Cleanup = ArrangeStalledCleanup(scenario.State);
        scenario.Entered = scenario.State.Entered!;
        return scenario;
    }

    private static async Task<Scenario> ArrangeReadinessFailureAsync()
    {
        var scenario = new Scenario();
        scenario.Cookie = Cookie(await Login(scenario.Client));
        scenario.State.BeforeStatement = (_, _) => throw new DatabaseUnavailableException("fake-sensitive-db-secret");
        return scenario;
    }

    private static void ArrangeRecovery(UserSessionTestHost.State state)
    {
        state.BeforeStatement = null;
        state.Entered = null;
    }

    private static async Task AssertReadinessRetryPendingAsync(UserSessionTestHost.State state)
    {
        await state.Clock.WaitForTimerAsync(1);
        Assert.Single(state.Forms);
    }

    private static async Task AdvanceToLastBudgetSecondAsync(UserSessionTestHost.State state, Task pending)
    {
        await state.Clock.WaitForTimerAsync(24);
        state.Clock.Advance(TimeSpan.FromSeconds(23));
        Assert.False(pending.IsCompleted);
    }

    private static async Task AdvanceToCleanupAsync(Scenario scenario)
    {
        await scenario.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        scenario.State.Clock.Advance(TimeSpan.FromSeconds(25));
        await scenario.Cleanup.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task AssertCleanupPendingAsync(UserSessionTestHost.State state, Task pending)
    {
        await state.Clock.WaitForTimerAsync(2);
        Assert.False(pending.IsCompleted);
    }

    private sealed class Scenario : IDisposable
    {
        public UserSessionTestHost.State State { get; } = new() { SharedTransport = true };
        public UserSessionTestHost Host { get; }
        private HttpClient? _client;
        public HttpClient Client => _client ??= UserSessionBoundaryTests.Client(Host);
        public string Cookie { get; set; } = "";
        public TaskCompletionSource Entered { get; set; } = null!;
        public TaskCompletionSource Cleanup { get; set; } = null!;

        public Scenario() => Host = new(State);

        public void Dispose()
        {
            _client?.Dispose();
            Host.Dispose();
        }
    }
}
