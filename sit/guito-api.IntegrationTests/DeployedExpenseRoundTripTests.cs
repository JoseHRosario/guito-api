using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.IntegrationTests.Common;

namespace GuitoApi.IntegrationTests;

/// <summary>
/// Business path of the DEPLOYED staging stack (issue #22): a positive agent-key
/// Expense create → list-latest round-trip against the live Google spreadsheet
/// backing staging, with a per-run unique marker.
/// </summary>
[Trait(DeployedEndpointFixture.CategoryTrait, DeployedEndpointFixture.CategoryValue)]
public class DeployedExpenseRoundTripTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExpenseRoundTrip_ShouldCreateThenListLatest_WhenStagingAgentKeyIsValid()
    {
        // Arrange — a unique per-run marker (digits survive ToTitleCase unchanged) so
        // repeated runs each match exactly their own row in the live sheet.
        using var client = DeployedEndpointFixture.CreateAgentClient(DeployedEndpointFixture.AgentKey);
        var marker = $"integration-test {DateTime.UtcNow:yyyyMMddHHmmss}";
        var expense = new
        {
            Date = DateTime.UtcNow,
            Amount = 0.01m,
            Description = marker,
            Category = "Integration Tests",
        };

        // Act
        var createResponse = await client.PostAsync("/expense", DeployedEndpointFixture.ToJsonContent(expense));
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