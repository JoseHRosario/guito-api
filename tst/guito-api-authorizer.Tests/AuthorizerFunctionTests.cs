using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using GuitoApiAuthorizer;
using GuitoApiAuthorizer.AgentKey;
using GuitoApiAuthorizer.GoogleToken;

namespace GuitoApiAuthorizer.Tests;

/// <summary>
/// Edge-dispatch tests for the Lambda REQUEST authorizer (issues #3/#4/#13):
/// `Authorization: Bearer <jwt>` → Google path, any other raw value → agent path,
/// nothing → Deny; every validation problem denies (fail closed). The gateway's sole
/// identity source is `Authorization` — no other header reaches the authorizer.
/// Fully hermetic: keys and tokens are faked via the validator seams.
/// </summary>
public class AuthorizerFunctionTests
{
    private static Function FunctionWith(IKeysLoader keysLoader, IGoogleTokenValidator? google = null) =>
        new(new AgentKeyValidator(keysLoader), google);

    private sealed class FakeKeysLoader(params string[] keys) : IKeysLoader
    {
        public Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(keys);
    }

    private sealed class ThrowingKeysLoader : IKeysLoader
    {
        public Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class StubGoogleValidator(bool valid) : IGoogleTokenValidator
    {
        public int Calls;
        public Task<GoogleTokenResult> ValidateAsync(string? idToken, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(valid
                ? new GoogleTokenResult(true, null, new GoogleTokenClaims("jose@example.com", "aud", "iss", 1))
                : new GoogleTokenResult(false, "rejected by stub", null));
        }
    }

    private sealed class NullLogger : ILambdaLogger
    {
        public void Log(string message) { }
        public void LogLine(string message) { }
    }

    private sealed class TestContext : ILambdaContext
    {
        public string AwsRequestId { get; } = "test";
        public IClientContext ClientContext { get; } = null!;
        public string FunctionName { get; } = "test-authorizer";
        public string FunctionVersion { get; } = "1";
        public ILambdaLogger Logger { get; } = new NullLogger();
        public string LogGroupName { get; } = "/test";
        public string LogStreamName { get; } = "stream";
        public ICognitoIdentity Identity { get; } = null!;
        public string InvokedFunctionArn { get; } = "arn:test";
        public int MemoryLimitInMB { get; } = 128;
        public TimeSpan RemainingTime => TimeSpan.FromSeconds(30);
    }

    private static APIGatewayCustomAuthorizerV2Request Request(Dictionary<string, string>? headers) =>
        new()
        {
            Type = "REQUEST",
            RouteArn = "arn:aws:execute-api:eu-west-1:497087877832:uv5jrq8moh/$default/GET/expense/latest/10",
            Headers = headers,
        };

    // --- agent path -------------------------------------------------------------------

    [Fact]
    public async Task FunctionHandler_ShouldReturnAllow_WhenApiKeyIsValid()
    {
        var function = FunctionWith(new FakeKeysLoader("alpha", "beta"));
        var response = await function.FunctionHandlerAsync(Request(new Dictionary<string, string>
        {
            ["Authorization"] = "beta",
        }), new TestContext());

        var statement = response.PolicyDocument.Statement.Single();
        Assert.Equal("Allow", statement.Effect);
        Assert.Equal("agent", response.PrincipalID);
        Assert.Contains("execute-api:Invoke", statement.Action);
        Assert.Contains("uv5jrq8moh", statement.Resource.Single());
    }

    [Fact]
    public async Task FunctionHandler_ShouldReturnAllow_WhenAuthorizationHeaderCaseDiffers()
    {
        var function = FunctionWith(new FakeKeysLoader("alpha"));
        var response = await function.FunctionHandlerAsync(Request(new Dictionary<string, string>
        {
            ["authorization"] = "alpha",
        }), new TestContext());

        Assert.Equal("Allow", response.PolicyDocument.Statement.Single().Effect);
    }

    [Fact]
    public async Task FunctionHandler_ShouldNotConsultGoogleValidator_WhenAgentKeyIsValid()
    {
        // Independence (ADR-0003): the Google validator is never consulted for the agent path.
        var google = new StubGoogleValidator(valid: true);
        var function = FunctionWith(new FakeKeysLoader("agent-key"), google);
        var response = await function.FunctionHandlerAsync(Request(new Dictionary<string, string>
        {
            ["Authorization"] = "agent-key",
        }), new TestContext());

        Assert.Equal("Allow", response.PolicyDocument.Statement.Single().Effect);
        Assert.Equal("agent", response.PrincipalID);
        Assert.Equal(0, google.Calls);
    }

    [Fact]
    public async Task FunctionHandler_ShouldReturnDeny_WhenApiKeyIsMissing()
    {
        var function = FunctionWith(new FakeKeysLoader("alpha"));
        var response = await function.FunctionHandlerAsync(Request([]), new TestContext());

        Assert.Equal("Deny", response.PolicyDocument.Statement.Single().Effect);
    }

    [Fact]
    public async Task FunctionHandler_ShouldReturnDeny_WhenApiKeyIsUnknown()
    {
        var function = FunctionWith(new FakeKeysLoader("alpha"));
        var response = await function.FunctionHandlerAsync(Request(new Dictionary<string, string>
        {
            ["Authorization"] = "intruder",
        }), new TestContext());

        Assert.Equal("Deny", response.PolicyDocument.Statement.Single().Effect);
        Assert.Equal("agent", response.PrincipalID);
        Assert.Equal("unknown X-Api-Key", response.Context["DenyReason"]);
    }

    [Fact]
    public async Task FunctionHandler_ShouldReturnDeny_WhenOnlyApiKeyHeaderIsPresent()
    {
        // X-Api-Key is NOT a gateway identity source: a REQUEST authorizer is never
        // invoked with it, so the dispatcher must not accept credentials from there.
        var function = FunctionWith(new FakeKeysLoader("alpha"));
        var response = await function.FunctionHandlerAsync(Request(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "alpha",
        }), new TestContext());

        Assert.Equal("Deny", response.PolicyDocument.Statement.Single().Effect);
    }

    [Fact]
    public async Task FunctionHandler_ShouldReturnDeny_WhenKeysListIsEmpty()
    {
        var function = FunctionWith(new FakeKeysLoader());
        var response = await function.FunctionHandlerAsync(Request(new Dictionary<string, string>
        {
            ["Authorization"] = "alpha",
        }), new TestContext());

        Assert.Equal("Deny", response.PolicyDocument.Statement.Single().Effect);
    }

    [Fact]
    public async Task FunctionHandler_ShouldReturnDeny_WhenKeysLoaderFails()
    {
        var function = FunctionWith(new ThrowingKeysLoader());
        var response = await function.FunctionHandlerAsync(Request(new Dictionary<string, string>
        {
            ["Authorization"] = "alpha",
        }), new TestContext());

        Assert.Equal("Deny", response.PolicyDocument.Statement.Single().Effect);
    }

    // --- human (Google) path ----------------------------------------------------------

    [Fact]
    public async Task FunctionHandler_ShouldReturnAllowHuman_WhenBearerTokenIsValid()
    {
        var function = FunctionWith(new FakeKeysLoader(["agent-key"]), new StubGoogleValidator(valid: true));
        var response = await function.FunctionHandlerAsync(Request(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer some-jwt",
        }), new TestContext());

        Assert.Equal("Allow", response.PolicyDocument.Statement.Single().Effect);
        Assert.Equal("human", response.PrincipalID);
    }

    [Fact]
    public async Task FunctionHandler_ShouldReturnDeny_WhenBearerTokenIsInvalid()
    {
        var function = FunctionWith(new FakeKeysLoader(["agent-key"]), new StubGoogleValidator(valid: false));
        var response = await function.FunctionHandlerAsync(Request(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer bad-token",
        }), new TestContext());

        Assert.Equal("Deny", response.PolicyDocument.Statement.Single().Effect);
        Assert.Equal("human", response.PrincipalID);
        Assert.Equal("rejected by stub", response.Context["DenyReason"]);
    }

    [Fact]
    public async Task FunctionHandler_ShouldReturnAllowHuman_WhenBearerPrefixCaseDiffers()
    {
        var function = FunctionWith(new FakeKeysLoader(["agent-key"]), new StubGoogleValidator(valid: true));
        var response = await function.FunctionHandlerAsync(Request(new Dictionary<string, string>
        {
            ["Authorization"] = "bearer some-jwt",
        }), new TestContext());

        Assert.Equal("Allow", response.PolicyDocument.Statement.Single().Effect);
        Assert.Equal("human", response.PrincipalID);
    }

    [Fact]
    public async Task FunctionHandler_ShouldNotConsultAgentKeyLoader_WhenBearerTokenIsValid()
    {
        // Independence (ADR-0003): the agent key loader is never consulted for the human path.
        var function = FunctionWith(new ThrowingKeysLoader(), new StubGoogleValidator(valid: true));
        var response = await function.FunctionHandlerAsync(Request(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer some-jwt",
        }), new TestContext());

        Assert.Equal("Allow", response.PolicyDocument.Statement.Single().Effect);
    }

    [Fact]
    public async Task FunctionHandler_ShouldReturnDeny_WhenNoAuthorizationHeaderIsPresent()
    {
        var function = FunctionWith(new FakeKeysLoader(["agent-key"]), new StubGoogleValidator(valid: true));
        var response = await function.FunctionHandlerAsync(Request(new Dictionary<string, string>
        {
            ["x-google-idtoken"] = "some-token",
        }), new TestContext());

        Assert.Equal("Deny", response.PolicyDocument.Statement.Single().Effect);
        Assert.Equal("anonymous", response.PrincipalID);
    }
}