using System.Text;
using System.Text.Json;

namespace GuitoApi.IntegrationTests.Common;

/// <summary>
/// Live configuration of the TARGET environment (staging by default, or
/// production), populated by scripts/run-staging-tests.sh from AWS Secrets
/// Manager before dotnet test starts. This process never reads AWS itself:
/// the runner is the only component that touches secrets, so no credential
/// logic is duplicated in the test code and keys exist only as process
/// environment variables for the duration of one run.
/// </summary>
public static class StagingEndpointFixture
{
    public const string CategoryTrait = "Category";
    public const string CategoryValue = "Integration";

    private static readonly Lazy<string> _baseUrl = new(RequiredEnvironment("GUITO_TARGET_BASE_URL"));
    private static readonly Lazy<string> _agentKey = new(RequiredEnvironment("GUITO_TARGET_AGENT_KEY"));
    private static readonly Lazy<string> _otherKey = new(RequiredEnvironment("GUITO_OTHER_AGENT_KEY"));
    private static readonly Lazy<string> _googleIdToken = new(RequiredEnvironment("GUITO_TARGET_GOOGLE_ID_TOKEN"));

    /// <summary>Root of the deployed TARGET environment stack (staging default, or production), e.g. https://abc123.execute-api.eu-west-1.amazonaws.com.</summary>
    public static string BaseUrl => _baseUrl.Value.TrimEnd('/');

    /// <summary>Agent key from the TARGET environment's secret (ApiKeys[0]) — the positive-path credential.</summary>
    public static string AgentKey => _agentKey.Value;

    /// <summary>Agent key from the OTHER environment's secret (ApiKeys[0]) — must be rejected by the target.</summary>
    public static string OtherKey => _otherKey.Value;

    /// <summary>
    /// Fresh Google ID token minted by scripts/run-staging-tests.sh (refresh-token
    /// exchange against secret guito-api/human-auth) — the positive-path human credential.
    /// </summary>
    public static string GoogleIdToken => _googleIdToken.Value;

    /// <summary>A plain client with no credentials attached.</summary>
    public static HttpClient CreateAnonymousClient()
    {
        var client = new HttpClient();
        client.BaseAddress = new Uri(BaseUrl);
        return client;
    }

    /// <summary>
    /// A client carrying the agent-key contract a real request needs: the BARE key
    /// (no scheme prefix) in <c>Authorization</c> — the gateway's single identity
    /// source, whose raw value the edge authorizer compares directly — and the same
    /// key in <c>X-Api-Key</c> for the in-app middleware. A scheme prefix (e.g.
    /// "Raw &lt;key&gt;") would be validated as part of the key itself and denied.
    /// </summary>
    public static HttpClient CreateAgentClient(string apiKey)
    {
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", apiKey);
        return client;
    }

    /// <summary>
    /// A client carrying the human-path contract a real request needs: the Google ID
    /// token in BOTH <c>Authorization: Bearer &lt;token&gt;</c> (the gateway's single
    /// identity source → edge Google validator) and <c>x-google-idtoken</c> for the
    /// in-app GoogleIdTokenMiddleware (ADR-0003: paths never fall back into each other).
    /// </summary>
    public static HttpClient CreateGoogleClient(string idToken)
    {
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {idToken}");
        client.DefaultRequestHeaders.Add("x-google-idtoken", idToken);
        return client;
    }

    /// <summary>Serializes <paramref name="payload"/> as the JSON request body for POST /expense.</summary>
    public static StringContent ToJsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(
            $"Environment variable {name} is not set — run scripts/run-staging-tests.sh instead of invoking dotnet test directly.");
}