using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using GuitoApiAuthorizer;

namespace GuitoApiAuthorizer.Tests;

/// <summary>
/// Unit tests for the X-Api-Key Lambda authorizer (issues #3/#4):
/// valid key → Allow, missing/unknown → Deny, keys-loader failure → Deny (fail closed).
/// The keys loader is faked via IKeysLoader — no network, no real secret access.
/// </summary>
public class AuthorizerFunctionTests
{
    private sealed class FakeKeysLoader(params string[] keys) : IKeysLoader
    {
        public Task<IReadOnlyList<string>> LoadAsync() => Task.FromResult<IReadOnlyList<string>>(keys);
    }

    private sealed class ThrowingKeysLoader : IKeysLoader
    {
        public Task<IReadOnlyList<string>> LoadAsync() => throw new InvalidOperationException("boom");
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

    [Fact]
    public async Task Valid_key_gets_allow_policy()
    {
        var function = new Function(new FakeKeysLoader("alpha", "beta"));
        var response = await function.FunctionHandler(Request(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "beta",
        }), new TestContext());

        var statement = response.PolicyDocument.Statement.Single();
        Assert.Equal("Allow", statement.Effect);
        Assert.Contains("execute-api:Invoke", statement.Action);
        Assert.Contains("uv5jrq8moh", statement.Resource.Single());
    }

    [Fact]
    public async Task Header_name_is_case_insensitive()
    {
        var function = new Function(new FakeKeysLoader("alpha"));
        var response = await function.FunctionHandler(Request(new Dictionary<string, string>
        {
            ["x-api-key"] = "alpha",
        }), new TestContext());

        Assert.Equal("Allow", response.PolicyDocument.Statement.Single().Effect);
    }

    [Fact]
    public async Task Missing_key_gets_deny()
    {
        var function = new Function(new FakeKeysLoader("alpha"));
        var response = await function.FunctionHandler(Request(new Dictionary<string, string>()), new TestContext());
        Assert.Equal("Deny", response.PolicyDocument.Statement.Single().Effect);
    }

    [Fact]
    public async Task Unknown_key_gets_deny()
    {
        var function = new Function(new FakeKeysLoader("alpha"));
        var response = await function.FunctionHandler(Request(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "intruder",
        }), new TestContext());
        Assert.Equal("Deny", response.PolicyDocument.Statement.Single().Effect);
    }

    [Fact]
    public async Task Empty_keys_list_fails_closed()
    {
        var function = new Function(new FakeKeysLoader());
        var response = await function.FunctionHandler(Request(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "alpha",
        }), new TestContext());
        Assert.Equal("Deny", response.PolicyDocument.Statement.Single().Effect);
    }

    [Fact]
    public async Task Keys_loader_failure_fails_closed()
    {
        var function = new Function(new ThrowingKeysLoader());
        var response = await function.FunctionHandler(Request(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "alpha",
        }), new TestContext());
        Assert.Equal("Deny", response.PolicyDocument.Statement.Single().Effect);
    }
}
