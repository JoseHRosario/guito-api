using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using GuitoApiAuthorizer.AgentKey;
using GuitoApiAuthorizer.GoogleToken;
using Xunit;

namespace GuitoApiAuthorizer.Tests;

public class SheetsReportingAuthorizerTests
{
    private const string RouteArn = "arn:aws:execute-api:eu-west-1:123:api/staging/GET/Expense/history";

    [Fact]
    public async Task Reporting_ShouldAllowOnlyRequestArn_WhenSignedInstallerRequestsHistoryAsync()
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        var function = FunctionWith(transport);
        var request = Request(transport.Token());

        // Act
        var response = await function.FunctionHandlerAsync(request, new Context());

        // Assert
        var statement = Assert.Single(response.PolicyDocument.Statement);
        Assert.Equal("Allow", statement.Effect);
        Assert.Equal(new[] { RouteArn }, statement.Resource);
        Assert.Equal("sheets-reporting", response.PrincipalID);
    }

    [Theory]
    [InlineData("POST", "/Expense/history")]
    [InlineData("OPTIONS", "/Expense/history")]
    [InlineData("GET", "/Expense")]
    [InlineData("GET", "/Expense/history/child")]
    [InlineData("GET", "/Expense/history/")]
    [InlineData("GET", "/Expense/history-more")]
    [InlineData("DELETE", "/Smoke/1")]
    public async Task Reporting_ShouldDenyOtherRoutes_WhenArnLooksLikeHistoryAsync(string method, string path)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        var function = FunctionWith(transport);
        var request = Request(transport.Token(), method, path);

        // Act
        var response = await function.FunctionHandlerAsync(request, new Context());

        // Assert
        Assert.Equal("Deny", Assert.Single(response.PolicyDocument.Statement).Effect);
        Assert.Equal(0, transport.Requests);
    }

    [Theory]
    [InlineData("sub", "other-subject")]
    [InlineData("sub", null)]
    [InlineData("email_verified", false)]
    [InlineData("email_verified", "true")]
    [InlineData("email_verified", null)]
    [InlineData("email", null)]
    [InlineData("iss", "https://evil.example")]
    [InlineData("exp", 1L)]
    [InlineData("aud", "other-client")]
    public async Task Reporting_ShouldDenyHistory_WhenSignedClaimsAreInvalidAsync(string claim, object? value)
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        var token = transport.Token(p => { if (value is null) p.Remove(claim); else p[claim] = value; });
        var function = FunctionWith(transport);
        var request = Request(token);

        // Act
        var response = await function.FunctionHandlerAsync(request, new Context());

        // Assert
        Assert.Equal("Deny", Assert.Single(response.PolicyDocument.Statement).Effect);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("", ReportingTokenFixture.Subject)]
    [InlineData(ReportingTokenFixture.Audience, "")]
    [InlineData(ReportingTokenFixture.Audience, " ")]
    [InlineData(ReportingTokenFixture.Audience, ReportingTokenFixture.Subject, ReportingTokenFixture.Audience)]
    public async Task Reporting_ShouldDenyHistory_WhenConfigurationIsDisabledOrOverlappingAsync(string audience, string subject, string humanAudience = "ui-client")
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        var function = FunctionWith(transport, audience, subject, humanAudience);
        var request = Request(transport.Token());

        // Act
        var response = await function.FunctionHandlerAsync(request, new Context());

        // Assert
        Assert.Equal("Deny", Assert.Single(response.PolicyDocument.Statement).Effect);
    }

    [Fact]
    public async Task Reporting_ShouldDenyHistory_WhenSignatureUsesUnknownKeyAsync()
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        var function = FunctionWith(transport);
        var request = Request(transport.Token(wrongSignature: true));

        // Act
        var response = await function.FunctionHandlerAsync(request, new Context());

        // Assert
        Assert.Equal("Deny", Assert.Single(response.PolicyDocument.Statement).Effect);
    }

    [Fact]
    public async Task Reporting_ShouldDenyHistory_WhenHeadersDisagreeAsync()
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        var function = FunctionWith(transport);
        var request = Request(transport.Token());
        request.Headers["x-google-idtoken"] = transport.Token(p => p["sub"] = "another");

        // Act
        var response = await function.FunctionHandlerAsync(request, new Context());

        // Assert
        Assert.Equal("Deny", Assert.Single(response.PolicyDocument.Statement).Effect);
    }

    [Fact]
    public async Task Reporting_ShouldDenyHistory_WhenHttpContextIsMissingAsync()
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        var function = FunctionWith(transport);
        var request = Request(transport.Token());
        request.RequestContext = null;

        // Act
        var response = await function.FunctionHandlerAsync(request, new Context());

        // Assert
        Assert.Equal("Deny", Assert.Single(response.PolicyDocument.Statement).Effect);
    }

    [Fact]
    public async Task Reporting_ShouldAllowCaseInsensitiveRoute_WhenArnIsDefaultAsync()
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        var request = Request(transport.Token(), path: "/expense/HISTORY");
        request.RouteArn = "arn:aws:execute-api:eu-west-1:123:api/staging/$default";
        var function = FunctionWith(transport);

        // Act
        var response = await function.FunctionHandlerAsync(request, new Context());

        // Assert
        Assert.Equal("Allow", Assert.Single(response.PolicyDocument.Statement).Effect);
        Assert.Equal(new[] { request.RouteArn }, Assert.Single(response.PolicyDocument.Statement).Resource);
    }

    [Fact]
    public async Task Human_ShouldRemainIndependent_WhenReportingIsConfiguredAsync()
    {
        // Arrange
        using var transport = new ReportingTokenFixture();
        var token = transport.Token(p => { p["aud"] = "ui-client"; p["email_verified"] = false; p["sub"] = "human-subject"; });
        var function = FunctionWith(transport);
        var request = Request(token, "POST", "/Expense");

        // Act
        var response = await function.FunctionHandlerAsync(request, new Context());

        // Assert
        Assert.Equal("Allow", Assert.Single(response.PolicyDocument.Statement).Effect);
        Assert.Equal("human", response.PrincipalID);
    }

    private static Function FunctionWith(ReportingTokenFixture transport, string audience = ReportingTokenFixture.Audience,
        string subject = ReportingTokenFixture.Subject, string humanAudience = "ui-client") =>
        new(new NoAgent(), new GoogleTokenValidator(new HttpJwksClient(new HttpClient(transport)), humanAudience, ["installer@example.com"]),
            new HttpJwksClient(new HttpClient(transport)), audience, subject, humanAudience);

    private static APIGatewayCustomAuthorizerV2Request Request(string token, string method = "GET", string path = "/Expense/history") => new()
    {
        RouteArn = RouteArn,
        Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
        {
            Http = new APIGatewayHttpApiV2ProxyRequest.HttpDescription { Method = method, Path = path },
        },
    };

    private sealed class NoAgent : IAgentKeyValidator
    {
        public Task<AgentKeyResult> ValidateAsync(string? key, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Reporting must never fall back to agent authorization");
    }

    private sealed class Context : ILambdaContext
    {
        public string AwsRequestId => "test";
        public IClientContext ClientContext => null!;
        public string FunctionName => "test";
        public string FunctionVersion => "test";
        public ICognitoIdentity Identity => null!;
        public string InvokedFunctionArn => "test";
        public ILambdaLogger Logger { get; } = new Logger();
        public string LogGroupName => "test";
        public string LogStreamName => "test";
        public int MemoryLimitInMB => 128;
        public TimeSpan RemainingTime => TimeSpan.FromMinutes(1);
    }
    private sealed class Logger : ILambdaLogger
    {
        public void Log(string message) { }
        public void LogLine(string message) { }
    }
}
