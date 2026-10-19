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
        // Arrange
        var state = new UserSessionTestHost.State { SharedTransport = true };
        state.BeforeStatement = (_, _) => throw (failure switch
        {
            "sql" => new BadRequestException("fake-sensitive-db-secret"),
            "credentials" => new Amazon.RDSDataService.AmazonRDSDataServiceException("fake-sensitive-db-secret") { ErrorCode = "AccessDeniedException" },
            _ => new HttpRequestException("fake-sensitive-db-secret")
        });
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var failed = await Login(client);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("temporarily_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal("SELECT 1", Assert.Single(state.Statements));
        Assert.Empty(state.Forms);
        Assert.Empty(state.Database.CredentialsWritten);
        Assert.DoesNotContain("fake-sensitive-db-secret", string.Join("\n", state.Logs));
        Assert.Empty(state.LoggedExceptions);
    }

    [Fact]
    public async Task Establish_ShouldNotReplayMutationOrCode_WhenDatabaseFailsAfterReadiness()
    {
        // Arrange
        var state = new UserSessionTestHost.State { SharedTransport = true };
        state.BeforeStatement = (request, _) => request.Sql.StartsWith("INSERT")
            ? throw new DatabaseUnavailableException("fake-sensitive-db-secret") : Task.CompletedTask;
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var failed = await Login(client);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("temporarily_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Single(state.Statements, sql => sql.StartsWith("INSERT"));
        Assert.Single(state.Forms);
        Assert.Empty(state.Database.CredentialsWritten);
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("fake-sensitive-db-secret", string.Join("\n", state.Logs));
        Assert.Empty(state.LoggedExceptions);
    }

    [Theory]
    [InlineData("database")]
    [InlineData("provider")]
    [InlineData("configuration")]
    [InlineData("google-configuration")]
    public async Task Establish_ShouldLogOnlyCategoricalStageAndType_WhenSensitiveFailureOccurs(string failure)
    {
        // Arrange
        var state = new UserSessionTestHost.State { SharedTransport = true };
        if (failure == "database") state.BeforeStatement = (_, _) => throw new InvalidOperationException("fake-sensitive-db-secret");
        if (failure == "provider") state.NetworkFailure = true;
        if (failure == "configuration") state.Key = "fake-sensitive-invalid-key";
        if (failure == "google-configuration") state.InvalidGoogleConfiguration = true;
        var signedToken = state.Token();
        state.Body = JsonSerializer.Serialize(new { id_token = signedToken, access_token = "fake-access-token",
            expires_in = 3600, refresh_token = "fake-refresh-credential" });
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var failed = await Login(client);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var logs = string.Join("\n", state.Logs);
        Assert.Contains("stage=", logs);
        Assert.Contains(failure switch { "database" => "datastore_unavailable", "provider" => "provider_unavailable", _ => "configuration_unavailable" }, logs);
        Assert.Contains("type=", logs);
        Assert.DoesNotContain("fake-sensitive", logs);
        Assert.DoesNotContain("fake-refresh-credential", logs);
        Assert.DoesNotContain("fake-code", logs);
        Assert.DoesNotContain("fake-access-token", logs);
        Assert.DoesNotContain("fake-client-secret", logs);
        Assert.DoesNotContain(new string('a', 43), logs);
        Assert.DoesNotContain(signedToken, logs);
        Assert.DoesNotContain(UserSessionTestHost.Email, logs);
        Assert.DoesNotContain(state.Key, logs);
        Assert.Empty(state.LoggedExceptions);
        Assert.Equal("temporarily_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Refresh_ShouldBoundAcquireAndPreserveSession_WhenLeaseTransportStalls()
    {
        // Arrange
        var state = new UserSessionTestHost.State { SharedTransport = true };
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);
        var cookie = Cookie(await Login(client));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.BeforeStatement = async (request, token) =>
        {
            if (!request.Sql.Contains("RETURNING subject")) return;
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        // Act
        var pending = Send(client, "refresh", cookie);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await state.Clock.WaitForTimerAsync(25);
        state.Clock.Advance(TimeSpan.FromSeconds(25));
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("temporarily_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.Single(state.Forms);
        Assert.Single(state.Database.CredentialsWritten);
        state.BeforeStatement = null;
        Assert.Contains("stage=lease_acquire category=request_timeout type=TaskCanceledException", string.Join("\n", state.Logs));
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "refresh", cookie)).StatusCode);
    }

    [Fact]
    public async Task Establish_ShouldWaitForSharedTransportResumeDelays_WhenDatabaseRecovers()
    {
        // Arrange
        var state = new UserSessionTestHost.State { SharedTransport = true };
        var attempts = 0;
        state.BeforeStatement = (request, _) =>
        {
            if (request.Sql == "SELECT 1" && ++attempts <= 2) throw new DatabaseResumingException("fake-sensitive-db-secret");
            return Task.CompletedTask;
        };
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var pending = Login(client);
        await state.Clock.WaitForTimerAsync(1);
        Assert.Empty(state.Forms);
        state.Clock.Advance(TimeSpan.FromSeconds(1));
        await state.Clock.WaitForTimerAsync(5);
        Assert.Empty(state.Forms);
        state.Clock.Advance(TimeSpan.FromSeconds(5));
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, attempts);
        Assert.Single(state.Forms);
        Assert.Single(state.Database.CredentialsWritten);
        Assert.Equal("SELECT 1", state.Statements[0]);
        Assert.Contains("category=database_resuming type=DatabaseResumingException", string.Join("\n", state.Logs));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Establish_ShouldReturnStartingWithoutConsumingCode_WhenKnownStartupExhaustsReadinessBudget(bool resuming)
    {
        // Arrange
        var state = new UserSessionTestHost.State { SharedTransport = true };
        state.BeforeStatement = (_, _) => throw (resuming
            ? new DatabaseResumingException("fake-sensitive-db-secret") : new DatabaseUnavailableException("fake-sensitive-db-secret"));
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var pending = Login(client);
        await state.Clock.WaitForTimerAsync(18);
        await state.Clock.WaitForTimerAsync(1);
        if (resuming)
        {
            state.Clock.Advance(TimeSpan.FromSeconds(1));
            await state.Clock.WaitForTimerAsync(5);
            state.Clock.Advance(TimeSpan.FromSeconds(5));
            await state.Clock.WaitForTimerAsync(1);
        }
        state.Clock.Advance(TimeSpan.FromSeconds(resuming ? 12 : 18));
        var failed = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
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
        state.BeforeStatement = null;
        Assert.Equal(HttpStatusCode.OK, (await Login(client)).StatusCode);
        Assert.Single(state.Forms);
    }

    [Fact]
    public async Task Establish_ShouldNotConsumeCode_WhenDatabaseReadinessFails()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        state.Database.Fail = true;
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var failed = await Login(client);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("temporarily_unavailable", (await failed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Empty(state.Forms);
        Assert.Empty(state.Database.CredentialsWritten);
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        state.Database.Fail = false;
        Assert.Equal(HttpStatusCode.OK, (await Login(client)).StatusCode);
        Assert.Single(state.Forms);
    }
}
