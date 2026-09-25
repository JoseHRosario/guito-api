using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GuitoApi.IntegrationTests;

/// <summary>
/// Speaks real HTTPS to the DEPLOYED staging stack (issue #22): proves the deploy
/// contract end-to-end across both auth gates — public /healthz, edge-authorizer
/// rejections, and a positive agent-key Expense round-trip against the live
/// Google spreadsheet backing staging.
///
/// Layer attribution (required by the issue's runner criterion) comes from the
/// response bodies:
///   • 401 {"message":"Unauthorized"} → the gateway rejected the request before
///     any authorizer ran: its single identity source (the Authorization header)
///     was absent;
///   • 401 "Missing API key"          → the edge passed the request, but the in-app
///     ApiKeyMiddleware saw no X-Api-Key;
///   • 403 {"message":"Forbidden"}    → the edge authorizer ran and denied the key
///     (a custom-authorizer Deny surfaces as the gateway's Forbidden body);
///   • 403 "Invalid API key"          → the edge passed, the in-app middleware
///     rejected the key — a different failure from an edge deny.
/// </summary>
[Trait(StagingEndpointFixture.CategoryTrait, StagingEndpointFixture.CategoryValue)]
public class StagingDeployContractTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Healthz_ShouldReturnOk_WhenCalledWithoutCredentials()
    {
        // Arrange
        using var client = StagingEndpointFixture.CreateAnonymousClient();

        // Act
        var response = await client.GetAsync("/healthz");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ExpenseEndpoint_ShouldReturnUnauthorizedFromGateway_WhenNoCredentialsArePresent()
    {
        // Arrange
        using var client = StagingEndpointClientWithoutAuthorizationHeader();

        // Act
        var response = await client.GetAsync("/Expense/latest/5");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Unauthorized", await response.Content.ReadAsStringAsync());
        // Gateway-generated body (no validator ran); the in-app middlewares produce different bodies.
    }

    [Fact]
    public async Task ExpenseEndpoint_ShouldReturnUnauthorizedFromAppMiddleware_WhenXApiKeyIsMissing()
    {
        // Arrange — the valid staging key in Authorization alone: the edge accepts,
        // the request reaches the app, and ApiKeyMiddleware rejects the missing
        // X-Api-Key with its own 401 body.
        using var client = StagingEndpointClientWithoutXApiKey();

        // Act
        var response = await client.GetAsync("/Expense/latest/5");

        // Assert — 401 {"message":"Unauthorized"} here would mean the gateway never
        // forwarded the request (identity-source contract broken), not a middleware
        // verdict; assert the middleware's body explicitly.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Missing API key", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExpenseEndpoint_ShouldReturnForbiddenFromEdgeAuthorizer_WhenProductionKeyIsSent()
    {
        // Arrange — the PROD agent key against the staging stack must be denied by
        // the edge authorizer (it only knows the staging key), never accepted.
        using var client = StagingEndpointFixture.CreateAgentClient(StagingEndpointFixture.ProdKey);

        // Act
        var response = await client.GetAsync("/Expense/latest/5");

        // Assert — a custom-authorizer Deny reaches the client as the gateway's
        // 403 {"message":"Forbidden"} (verified against the authorizer's live log
        // "Agent key rejected: unknown X-Api-Key"). 403 with the in-app middleware's
        // "Invalid API key" body would instead mean the edge let a foreign key
        // through and the app denied it — a different failure.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Forbidden", body);
        Assert.DoesNotContain("Invalid API key", body);
    }

    [Fact]
    public async Task ExpenseRoundTrip_ShouldCreateThenListLatest_WhenStagingAgentKeyIsValid()
    {
        // Arrange — a unique per-run marker (digits survive ToTitleCase unchanged) so
        // repeated runs each match exactly their own row in the live sheet.
        using var client = StagingEndpointFixture.CreateAgentClient(StagingEndpointFixture.AgentKey);
        var marker = $"integration-test {DateTime.UtcNow:yyyyMMddHHmmss}";
        var expense = new
        {
            Date = DateTime.UtcNow,
            Amount = 0.01m,
            Description = marker,
            Category = "Integration Tests",
        };

        // Act
        var createResponse = await client.PostAsync("/expense", StagingEndpointFixture.ToJsonContent(expense));
        var listResponse = await client.GetAsync("/Expense/latest/5");

        // Assert — both calls succeed, and the listed payload round-trips through
        // the live Google spreadsheet. The description comes back TitleCased
        // ("Integration-Test ..."); the sheet stores the date day-precision.
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var latest = await listResponse.Content.ReadFromJsonAsync<ExpenseListLatest>(_jsonOptions);
        Assert.NotNull(latest);
        var expenses = latest!.Expenses ?? throw new InvalidOperationException("list payload had no Expenses array");
        var seen = Assert.Single(expenses, e =>
            e.Amount == expense.Amount &&
            e.Category == expense.Category &&
            e.Description?.Contains(marker["integration-test ".Length..], StringComparison.Ordinal) == true &&
            e.Date >= expense.Date.Date);
    }

    /// <summary>
    /// Deliberately omits the raw Authorization value — API Gateway's only identity
    /// source — while sending an (irrelevant) X-Api-Key, so the rejection happens at
    /// the gateway itself, before any authorizer or middleware. Distinct from a
    /// key-validation failure and from the in-app missing-key case below.
    /// </summary>
    private static HttpClient StagingEndpointClientWithoutAuthorizationHeader()
    {
        var client = StagingEndpointFixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "irrelevant-missing-authorization-header");
        return client;
    }

    /// <summary>
    /// Deliberately omits X-Api-Key while presenting the valid staging key as the
    /// raw Authorization value: the edge authorizer accepts it, and the request
    /// reaches the app, whose ApiKeyMiddleware rejects it with 401 "Missing API key"
    /// (verified live) — the in-app half of the deploy contract.
    /// </summary>
    private static HttpClient StagingEndpointClientWithoutXApiKey()
    {
        var client = StagingEndpointFixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", StagingEndpointFixture.AgentKey);
        return client;
    }

    private sealed class ExpenseListLatest
    {
        public List<ExpenseListLatestDetail>? Expenses { get; set; }
    }

    private sealed class ExpenseListLatestDetail
    {
        public DateTime? Date { get; set; }
        public decimal? Amount { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
    }
}
