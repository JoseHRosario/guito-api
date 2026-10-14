using System.Text.Json;

namespace GuitoApi.IntegrationTests.Common;

/// <summary>Defense in depth: even negative-auth mutations require disposable staging.</summary>
public sealed class DeployedRequestGuard : DelegatingHandler
{
    public DeployedRequestGuard() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }

    public DeployedRequestGuard(HttpMessageHandler transport) : base(transport) { }

    public static string RequireIsolation()
    {
        if (Environment.GetEnvironmentVariable("GUITO_TARGET_ENVIRONMENT") != "staging" ||
            Environment.GetEnvironmentVariable("GUITO_TEST_MODE") != "round-trip")
            throw new InvalidOperationException("Mutations require explicitly isolated staging; production is read-only.");
        using var document = JsonDocument.Parse(Environment.GetEnvironmentVariable("GUITO_STAGING_ISOLATION")
            ?? throw new InvalidOperationException("Run the isolation-gated deployed test runner."));
        var gate = document.RootElement;
        if (gate.GetProperty("environment").GetString() != "staging" ||
            gate.GetProperty("baseUrl").GetString()?.TrimEnd('/') != DeployedEndpointFixture.BaseUrl ||
            !gate.GetProperty("disposable").GetBoolean() || !gate.GetProperty("noPersonalData").GetBoolean() ||
            !gate.GetProperty("exclusiveDatabaseCredentials").GetBoolean() ||
            !gate.GetProperty("syntheticCreateDeleteApproved").GetBoolean() ||
            gate.GetProperty("expiresAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Missing, expired or mismatched disposable staging attestation.");
        return gate.GetProperty("category").GetString()
            ?? throw new InvalidOperationException("An existing staging category must be approved.");
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var target = new Uri(DeployedEndpointFixture.BaseUrl);
        if (request.RequestUri is not { } uri || uri.GetLeftPart(UriPartial.Authority) != target.GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("Deployed tests cannot contact a different origin.");
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Options)
            RequireIsolation();
        return base.SendAsync(request, cancellationToken);
    }
}
