using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Tests;

public class ApiBoundaryTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ApiBoundaryTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Healthz_returns_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_expense_appends_to_spreadsheet()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/expense", new ExpenseCreate
        {
            Date = new DateTime(2026, 9, 21),
            Amount = 42.50m,
            Description = "coffee shop",
            Category = "Restaurants",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var sheets = _factory.SheetsHandler;
        var appendBody = sheets.AppendBodies.Last();
        Assert.Contains("Coffee Shop", appendBody);
        Assert.Contains("42.5", appendBody);
        Assert.Contains("Restaurants", appendBody);
        Assert.Contains("2026-09-21", appendBody);
        // The year/month formulas are written to the appended row
        Assert.Contains($"ExpensesAux!C{FakeSheetsHttpHandler.NextRowIndex}", sheets.UpdateRanges.Last());
    }

    [Fact]
    public async Task List_latest_expenses_reads_spreadsheet()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/expense/latest/10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ExpenseListLatest>();
        Assert.NotNull(result);
        Assert.Equal(3, result!.Expenses.Count);
        var first = result.Expenses[0];
        Assert.Equal(12.50m, first.Amount);
        Assert.Equal("Coffee", first.Description);
        Assert.Equal("Restaurants", first.Category);
        Assert.Equal(new DateTime(2026, 9, 20), first.Date);
    }

    [Fact]
    public async Task Match_expenses_combines_transactions_and_expenses()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/expense/match");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<ExpenseMatchList>();
        Assert.NotNull(result);
        Assert.NotEmpty(result!.Matches);
        // Canned bank transactions and stored expenses both surface in the match list
        Assert.Contains(result.Matches, m => m.Transaction is not null);
        Assert.Contains(result.Matches, m => m.Expense is not null);
    }

    [Fact]
    public async Task List_categories_reads_spreadsheet()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/category");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<CategoryList>();
        Assert.NotNull(result);
        Assert.Equal(["Groceries", "Restaurants", "Transport"], result!.Categories.Select(c => c.Name));
    }

    [Fact]
    public async Task Extract_returns_501_with_clear_message()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/ai/extract", new ExpenseExtract
        {
            Prompt = "coffee for 3 euros yesterday",
            Language = "en-US",
        });

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("not implemented", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Google_id_token_validation_rejects_requests_without_header()
    {
        var client = _factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AppConfiguration:Authentication:ValidateIdToken"] = "true",
                }))).CreateClient();

        var response = await client.GetAsync("/expense/latest/1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}