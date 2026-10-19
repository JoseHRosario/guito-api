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
        // Arrange
        var state = new UserSessionTestHost.State { SharedTransport = true };
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.BeforeStatement = async (request, token) =>
        {
            var blocked = stage switch
            {
                "readiness" => request.Sql == "SELECT 1",
                "acquire" => request.Sql.Contains("RETURNING subject"),
                "completion" => request.Sql.Contains("SET encrypted_credential"),
                _ => false
            };
            if (!blocked) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        if (stage == "provider") { state.Entered = entered; state.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        using var cancelled = new CancellationTokenSource();

        // Act
        var pending = host.Server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/Auth/session/refresh";
            context.Request.Scheme = "https";
            context.Request.Headers["Origin"] = UserSessionTestHost.Origin;
            context.Request.Headers["X-Guito-Session"] = "1";
            context.Request.Headers["Cookie"] = cookie;
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}"));
            context.RequestAborted = cancelled.Token;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancelled.Cancel();
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(503, failed.Response.StatusCode);
        Assert.False(failed.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Contains("category=client_cancelled", string.Join("\n", state.Logs));
        Assert.Single(state.Database.CredentialsWritten);
        Assert.Empty(state.LoggedExceptions);
        state.BeforeStatement = null;
        state.Entered = null;
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "refresh", cookie)).StatusCode);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("completion")]
    public async Task Refresh_ShouldShareOneBudgetAndPreserveSession_WhenLaterStageStalls(string stage)
    {
        // Arrange
        var state = new UserSessionTestHost.State { SharedTransport = true };
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readinessAttempts = 0;
        state.BeforeStatement = async (request, token) =>
        {
            if (request.Sql == "SELECT 1" && ++readinessAttempts == 1) throw new DatabaseUnavailableException("fake-sensitive-db-secret");
            if (stage != "completion" || !request.Sql.Contains("SET encrypted_credential")) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        if (stage == "provider") { state.Entered = entered; state.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously); }

        // Act
        var pending = Send(client, "refresh", cookie);
        await state.Clock.WaitForTimerAsync(1);
        Assert.Single(state.Forms);
        state.Clock.Advance(TimeSpan.FromSeconds(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await state.Clock.WaitForTimerAsync(24);
        state.Clock.Advance(TimeSpan.FromSeconds(23));
        Assert.False(pending.IsCompleted);
        state.Clock.Advance(TimeSpan.FromSeconds(1));
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await AssertTemporaryAsync(failed);
        Assert.Single(state.Database.CredentialsWritten);
        Assert.Equal(2, state.Forms.Count);
        Assert.Contains($"stage={(stage == "provider" ? "provider_refresh" : "session_complete")} category=request_timeout", string.Join("\n", state.Logs));
        Assert.Empty(state.LoggedExceptions);
        state.BeforeStatement = null;
        state.Entered = null;
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "refresh", cookie)).StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldAwaitAtMostTwoSecondsOfCleanup_WhenBudgetExpiresDuringProviderCall()
    {
        // Arrange
        var state = new UserSessionTestHost.State { SharedTransport = true };
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.BeforeStatement = async (request, token) =>
        {
            if (!request.Sql.StartsWith("UPDATE user_sessions SET refresh_lease = NULL")) return;
            cleanup.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        // Act
        var pending = Send(client, "refresh", cookie);
        await state.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        state.Clock.Advance(TimeSpan.FromSeconds(25));
        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await state.Clock.WaitForTimerAsync(2);
        Assert.False(pending.IsCompleted);
        state.Clock.Advance(TimeSpan.FromSeconds(2));
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await AssertTemporaryAsync(failed);
        Assert.Equal(TimeSpan.FromSeconds(27), TimeSpan.FromTicks(state.Clock.GetTimestamp()));
        Assert.Single(state.Database.CredentialsWritten);
        Assert.Contains("stage=lease_release", string.Join("\n", state.Logs));
        Assert.Empty(state.LoggedExceptions);
        state.BeforeStatement = null;
        state.Entered = null;
        await AssertTemporaryAsync(await Send(client, "refresh", cookie));
        state.Clock.Now = state.Clock.Now.AddMinutes(2);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "refresh", cookie)).StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldPreserveCookieAndCredential_WhenReadinessStartupBudgetExhausts()
    {
        // Arrange
        var state = new UserSessionTestHost.State { SharedTransport = true };
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        state.BeforeStatement = (_, _) => throw new DatabaseUnavailableException("fake-sensitive-db-secret");

        // Act
        var pending = Send(client, "refresh", cookie);
        await state.Clock.WaitForTimerAsync(1);
        state.Clock.Advance(TimeSpan.FromSeconds(18));
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("service_starting", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.Single(state.Forms);
        Assert.Single(state.Database.CredentialsWritten);
        state.BeforeStatement = null;
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "refresh", cookie)).StatusCode);
    }

    [Theory]
    [InlineData("readiness")]
    [InlineData("provider")]
    [InlineData("persistence")]
    public async Task Establish_ShouldRespectDeadlineWithoutReplayingCode_WhenStageStalls(string stage)
    {
        // Arrange
        var state = new UserSessionTestHost.State { SharedTransport = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.BeforeStatement = async (request, token) =>
        {
            if (!(stage == "readiness" && request.Sql == "SELECT 1") && !(stage == "persistence" && request.Sql.StartsWith("INSERT"))) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        if (stage == "provider") { state.Entered = entered; state.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var pending = Login(client);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        state.Clock.Advance(TimeSpan.FromSeconds(stage == "readiness" ? 18 : 25));
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await AssertTemporaryAsync(failed);
        Assert.Equal(stage == "readiness" ? 0 : 1, state.Forms.Count);
        Assert.Contains("category=request_timeout", string.Join("\n", state.Logs));
        Assert.Empty(state.Database.CredentialsWritten);
        Assert.Empty(state.LoggedExceptions);
    }

    private static async Task AssertTemporaryAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("temporarily_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
}
