using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using static GuitoApi.Tests.UserSessionBoundaryTests;

namespace GuitoApi.Tests;

public class UserSessionDynamoDbTests
{
    [Theory]
    [InlineData("table")]
    [InlineData("region")]
    public async Task Establish_ShouldFailClosedBeforeCodeExchange_WhenStorageConfigurationIsMissing(string field)
    {
        // Arrange
        using var scenario = new Scenario();
        if (field == "table") scenario.State.TableName = "";
        else scenario.State.Region = "";

        // Act
        var response = await Login(scenario.Client);

        // Assert
        await AssertTemporaryAsync(response);
        Assert.Empty(scenario.State.Forms);
        Assert.Empty(scenario.State.DynamoRequests);
    }

    [Theory]
    [InlineData(-1, 200)]
    [InlineData(0, 401)]
    [InlineData(1, 401)]
    public async Task Refresh_ShouldPreserveFullExpiryPrecision_WhenIdleBoundaryIsWithinOneTick(int ticks, int status)
    {
        // Arrange
        using var scenario = new Scenario();
        scenario.State.Clock.Now = scenario.State.Clock.Now.AddTicks(1234567);
        var cookie = Cookie(await Login(scenario.Client));
        scenario.State.Clock.Now = scenario.State.Clock.Now.AddDays(7).AddTicks(ticks);

        // Act
        var response = await Send(scenario.Client, "refresh", cookie);

        // Assert
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldUpdateTtlOnlyAfterSuccess_WhenProviderFailsBeforeRecovery()
    {
        // Arrange
        using var scenario = new Scenario();
        var cookie = Cookie(await Login(scenario.Client));
        scenario.State.Clock.Now = scenario.State.Clock.Now.AddDays(6);
        scenario.State.NetworkFailure = true;

        // Act
        var failed = await Send(scenario.Client, "refresh", cookie);
        scenario.State.NetworkFailure = false;
        var recovered = await Send(scenario.Client, "refresh", cookie);

        // Assert
        await AssertTemporaryAsync(failed);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        AssertTtlWrites(scenario.State);
    }

    [Fact]
    public async Task Refresh_ShouldReleaseCommittedLease_WhenAcquireResponseIsLost()
    {
        // Arrange
        using var scenario = new Scenario();
        var cookie = Cookie(await Login(scenario.Client));
        scenario.State.AfterDynamoRequest = (request, _) => request is UpdateItemRequest { ReturnValues.Value: "ALL_NEW" }
            ? throw new HttpRequestException("fake-sensitive-credential") : Task.CompletedTask;

        // Act
        var failed = await Send(scenario.Client, "refresh", cookie);
        scenario.State.AfterDynamoRequest = null;
        var recovered = await Send(scenario.Client, "refresh", cookie);

        // Assert
        await AssertTemporaryAsync(failed);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, scenario.State.Forms.Count);
    }

    [Fact]
    public async Task Refresh_ShouldPreserveCookie_WhenExpiredCleanupLosesConcurrentRenewalRace()
    {
        // Arrange
        using var scenario = new Scenario();
        var cookie = Cookie(await Login(scenario.Client));
        scenario.State.Clock.Now = scenario.State.Clock.Now.AddDays(7);
        ArrangeRenewalBeforeCleanup(scenario, cookie);

        // Act
        var response = await Send(scenario.Client, "refresh", cookie);
        var recovered = await Send(scenario.Client, "refresh", cookie);

        // Assert
        await AssertTemporaryAsync(response);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task Refresh_ShouldRejectCompletion_WhenLeaseExpiresDuringProviderCall()
    {
        // Arrange
        using var scenario = new Scenario();
        var cookie = Cookie(await Login(scenario.Client));
        ArrangeProviderPause(scenario.State);
        var pending = Send(scenario.Client, "refresh", cookie);
        await scenario.State.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        scenario.State.Clock.Now = scenario.State.Clock.Now.AddMinutes(2);
        scenario.State.Continue!.SetResult();
        var response = await pending;

        // Assert
        await AssertTemporaryAsync(response);
        Assert.Single(scenario.State.Database.CredentialsWritten);
    }

    [Theory]
    [InlineData("readiness")]
    [InlineData("acquire")]
    [InlineData("completion")]
    [InlineData("logout")]
    public async Task Session_ShouldPreserveCookieAndIdleExpiry_WhenDynamoDbIsThrottled(string stage)
    {
        // Arrange
        using var scenario = new Scenario();
        var cookie = Cookie(await Login(scenario.Client));
        scenario.State.Clock.Now = scenario.State.Clock.Now.AddDays(6);
        ArrangeThrottling(scenario.State, stage);

        // Act
        var failed = await Send(scenario.Client, stage == "logout" ? "logout" : "refresh", cookie);
        scenario.State.BeforeDynamoRequest = null;
        scenario.State.Clock.Now = scenario.State.Clock.Now.AddDays(1);
        var expired = await Send(scenario.Client, "refresh", cookie);

        // Assert
        await AssertTemporaryAsync(failed);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("success")]
    [InlineData("invalid-grant")]
    public async Task Refresh_ShouldKeepNewerActiveLease_WhenObsoleteWorkerFinishes(string outcome)
    {
        // Arrange
        using var scenario = new Scenario();
        var cookie = Cookie(await Login(scenario.Client));
        var workers = await ArrangeTwoWorkersAsync(scenario, cookie, outcome);

        // Act
        workers.ObsoleteGate.SetResult();
        var obsolete = await workers.Obsolete;
        var competitor = await Send(scenario.Client, "refresh", cookie).WaitAsync(TimeSpan.FromSeconds(5));
        workers.CurrentGate.SetResult();
        var current = await workers.Current;

        // Assert
        await AssertTemporaryAsync(obsolete);
        await AssertTemporaryAsync(competitor);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    private static async Task<Workers> ArrangeTwoWorkersAsync(Scenario scenario, string cookie, string outcome)
    {
        var state = scenario.State;
        state.NetworkFailure = outcome == "network";
        state.Status = outcome == "invalid-grant" ? HttpStatusCode.BadRequest : HttpStatusCode.OK;
        state.Body = outcome == "invalid-grant" ? "{\"error\":\"invalid_grant\"}" : null;
        ArrangeProviderPause(state);
        var obsoleteGate = state.Continue!;
        var obsolete = Send(scenario.Client, "refresh", cookie);
        await state.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        state.Clock.Now = state.Clock.Now.AddMinutes(3);
        state.NetworkFailure = false;
        state.Status = HttpStatusCode.OK;
        state.Body = null;
        ArrangeProviderPause(state);
        var current = Send(scenario.Client, "refresh", cookie);
        await state.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return new(obsolete, current, obsoleteGate, state.Continue!);
    }

    private sealed record Workers(Task<HttpResponseMessage> Obsolete, Task<HttpResponseMessage> Current,
        TaskCompletionSource ObsoleteGate, TaskCompletionSource CurrentGate);

    private static void AssertTtlWrites(UserSessionTestHost.State state)
    {
        var created = Assert.Single(state.DynamoRequests.OfType<PutItemRequest>());
        Assert.Equal("1894104000", created.Item["ttl"].N);
        var completed = Assert.Single(state.DynamoRequests.OfType<UpdateItemRequest>(), request => request.UpdateExpression.Contains("ttl ="));
        Assert.Contains("#ttl = :ttl", completed.UpdateExpression);
        Assert.Equal("ttl", completed.ExpressionAttributeNames["#ttl"]);
        Assert.Equal("1894622400", completed.ExpressionAttributeValues[":ttl"].N);
        Assert.Equal(new[] { "1894104000", "1894622400" }, state.Database.TtlsWritten);
        Assert.DoesNotContain(state.DynamoRequests.OfType<UpdateItemRequest>().Where(request => request != completed),
            request => request.ExpressionAttributeValues.ContainsKey(":ttl"));
    }

    private static void ArrangeRenewalBeforeCleanup(Scenario scenario, string cookie)
    {
        scenario.State.BeforeDynamoRequest = async (request, _) =>
        {
            if (request is not DeleteItemRequest delete || !delete.ConditionExpression.Contains("expires_at_ticks")) return;
            scenario.State.BeforeDynamoRequest = null;
            scenario.State.Clock.Now = scenario.State.Clock.Now.AddTicks(-1);
            Assert.Equal(HttpStatusCode.OK, (await Send(scenario.Client, "refresh", cookie)).StatusCode);
        };
    }

    private static void ArrangeThrottling(UserSessionTestHost.State state, string stage)
    {
        state.BeforeDynamoRequest = (request, _) =>
        {
            var matches = stage switch
            {
                "readiness" => request is GetItemRequest,
                "acquire" => request is UpdateItemRequest { ReturnValues.Value: "ALL_NEW" },
                "completion" => request is UpdateItemRequest update && update.UpdateExpression.StartsWith("SET encrypted_credential"),
                _ => request is DeleteItemRequest
            };
            return matches ? throw new ProvisionedThroughputExceededException("fake-sensitive-credential") : Task.CompletedTask;
        };
    }

    private static void ArrangeProviderPause(UserSessionTestHost.State state)
    {
        state.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task AssertTemporaryAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("temporarily_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.DoesNotContain("fake-sensitive", await response.Content.ReadAsStringAsync());
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
