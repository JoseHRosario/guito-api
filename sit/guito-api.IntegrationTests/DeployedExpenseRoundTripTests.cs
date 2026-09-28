using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.IntegrationTests.Common;

namespace GuitoApi.IntegrationTests;

/// <summary>
/// Business path of the DEPLOYED stack (issue #22), exercised per ADR-0009
/// entirely inside the environment's Smoke Test tab: the expense round-trip
/// creates, lists, then deletes its own row (asserting it is gone after), so
/// real tabs are never written and no integration-test rows accumulate. A
/// separate read-only smoke covers match and category against the real tabs.
/// </summary>
[Trait(DeployedEndpointFixture.CategoryTrait, DeployedEndpointFixture.CategoryValue)]
public class DeployedExpenseRoundTripTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExpenseRoundTrip_ShouldCreateListThenDelete_WhenScopedToSmokeTab()
    {
        // Arrange — a smoke-scoped agent client (X-Sheet-Scope redirects writes and list
        // reads to the Smoke Test tab) and a per-run unique marker (digits survive
        // ToTitleCase unchanged) so repeated runs match exactly their own row.
        using var client = DeployedEndpointFixture.CreateAgentClient(DeployedEndpointFixture.AgentKey);
        client.DefaultRequestHeaders.Add("X-Sheet-Scope", "smoke");
        var marker = $"integration-test {DateTime.UtcNow:yyyyMMddHHmmss}";
        var expense = new ExpensePayload
        {
            Date = DateTime.UtcNow,
            Amount = 0.01m,
            Description = marker,
            Category = "Integration Tests",
        };

        // Act — create, list, delete, list again.
        var createResponse = await client.PostAsync("/expense", DeployedEndpointFixture.ToJsonContent(expense));
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var id = await ReadCreatedIdAsync(createResponse);

        var listResponse = await client.GetAsync("/Expense/latest/5");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listedBefore = await IsListedAsync(listResponse, expense, marker);
        Assert.True(listedBefore, "created expense was not listed before delete");

        // DELETE /Smoke/{id} is scoped to the Smoke Test tab by the route itself.
        var deleteResponse = await client.DeleteAsync($"/Smoke/{id}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var finalListResponse = await client.GetAsync("/Expense/latest/5");
        Assert.Equal(HttpStatusCode.OK, finalListResponse.StatusCode);
        var listedAfter = await IsListedAsync(finalListResponse, expense, marker);

        // Assert — the row came back after the delete (sheet back to pre-run state for this row).
        Assert.False(listedAfter, "deleted expense is still listed after DELETE /Smoke");
    }

 
    [Fact]
    public async Task Categories_ShouldSucceed_WhenReadingRealTab()
    {
        // Read-only smoke of the category read anchor (real tab, per ADR-0009).
        using var client = DeployedEndpointFixture.CreateAgentClient(DeployedEndpointFixture.AgentKey);

        var response = await client.GetAsync("/Category");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<int> ReadCreatedIdAsync(HttpResponseMessage createResponse)
    {
        var created = (await createResponse.Content.ReadFromJsonAsync<ExpenseCreated>(_jsonOptions))!;
        return created.Id ?? throw new InvalidOperationException("POST /expense returned no Id");
    }

    /// <summary>True when the live list contains this round-trip's own row (marker + amount + category).</summary>
    private static async Task<bool> IsListedAsync(HttpResponseMessage response, ExpensePayload expense, string marker)
    {
        var latest = (await response.Content.ReadFromJsonAsync<ExpenseListLatest>(_jsonOptions))!;
        var expenses = latest.Expenses ?? throw new InvalidOperationException("list payload had no Expenses array");
        return expenses.Any(e =>
            e.Amount == expense.Amount &&
            e.Category == expense.Category &&
            e.Description?.Contains(marker["integration-test ".Length..], StringComparison.Ordinal) == true);
    }

    private sealed class ExpensePayload
    {
        public DateTime Date { get; set; }
        public decimal Amount { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
    }

    private sealed class ExpenseCreated
    {
        public int? Id { get; set; }
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