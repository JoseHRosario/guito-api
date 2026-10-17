using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GuitoApi.Tests;

public class BankCreateAndMatchBoundaryTests
{
    private const string Path = "/BankTransaction/9223372036854775807/create-and-match";

    private static CustomWebApplicationFactory Factory()
    {
        var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true, UseRealBankTransactionRepository = true };
        factory.Postgres.HasBankReviewSchema = true;
        factory.Postgres.NextTransactionId = "create-match";
        factory.Postgres.LinkedTransactions.Add(long.MaxValue, (null, "needsReview"));
        return factory;
    }

    private static object Request(string amount = "-98.7654321", string date = "2026-08-20T23:45:12.123456+02:00") => new
    {
        amount, bookingDate = date,
        description = "Edited purchase", category = "Groceries", currency = "EUR"
    };

    [Fact]
    public async Task CreateAndMatch_ShouldPersistEditedExpenseAndCurrentBankState_WhenSaveIsExplicitAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(Path, Request());

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bank = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(long.MaxValue, bank.GetProperty("id").GetInt64());
        Assert.Equal("matched", bank.GetProperty("reviewStatus").GetString());
        Assert.Equal(1, bank.GetProperty("linkedExpense").GetProperty("id").GetInt32());
        var current = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal(current.GetRawText(), bank.GetRawText());
        Assert.False(bank.TryGetProperty("transactionId", out _));
        await AssertEditedExpenseAsync(client);
        await AssertSingleMatchedExpenseAsync(client);
    }

    private static async Task AssertEditedExpenseAsync(HttpClient client)
    {
        var expense = await client.GetFromJsonAsync<JsonElement>("/Expense/1");
        Assert.Equal("-98.7654321", expense.GetProperty("amount").GetString());
        Assert.Equal("2026-08-20T22:45:12.123456+01:00", expense.GetProperty("bookingDate").GetString());
        Assert.Equal("Edited purchase", expense.GetProperty("description").GetString());
    }

    private static async Task AssertSingleMatchedExpenseAsync(HttpClient client)
    {
        var bank = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal(1, bank.GetProperty("linkedExpense").GetProperty("id").GetInt32());
        Assert.Equal("matched", bank.GetProperty("reviewStatus").GetString());
        var expenses = await client.GetFromJsonAsync<JsonElement>("/Expense/latest/100");
        Assert.Single(expenses.GetProperty("expenses").EnumerateArray());
    }

    private static async Task AssertUnlinkedBankAsync(HttpClient client)
    {
        var bank = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal("needsReview", bank.GetProperty("reviewStatus").GetString());
        Assert.Equal(JsonValueKind.Null, bank.GetProperty("linkedExpense").ValueKind);
    }

    [Fact]
    public async Task CreateAndMatch_ShouldRejectSecondSubmissionWithoutOrphanOrOverwrite_WhenAlreadyMatchedAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync(Path, Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var original = await client.GetStringAsync("/BankTransaction");

        // Act
        using var second = await client.PostAsJsonAsync(Path, Request("100", "2026-07-01"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(original, await client.GetStringAsync("/BankTransaction"));
        await AssertSingleMatchedExpenseAsync(client);
        await AssertEditedExpenseAsync(client);
    }

    [Fact]
    public async Task CreateAndMatch_ShouldExposeCurrentStateThroughExistingRead_WhenCommitResponseIsLostAsync()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.CommitAppliedThenThrow = new TimeoutException("Commit response lost");
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(Path, Request());

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("unknown", await response.Content.ReadAsStringAsync());
        await AssertSingleMatchedExpenseAsync(client);
        await AssertEditedExpenseAsync(client);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("direction")]
    [InlineData("amount")]
    [InlineData("nan")]
    [InlineData("infiniteDate")]
    [InlineData("unsupportedDate")]
    [InlineData("currency")]
    [InlineData("ignored")]
    [InlineData("matched")]
    [InlineData("linked")]
    public async Task CreateAndMatch_ShouldRejectUnsupportedOrStaleBank_WhenMovementCannotBeLinkedAsync(string state)
    {
        // Arrange
        using var factory = Factory();
        SetBankState(factory, state);
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(Path, Request());

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertNoCreationAsync(client);
    }

    private static void SetBankState(CustomWebApplicationFactory factory, string state)
    {
        factory.Postgres.BankMovementStates[long.MaxValue] = (state == "status" ? "PDNG" : "BOOK", state == "direction" ? "CRDT" : "DBIT");
        factory.Postgres.BankDetails[long.MaxValue] = (
            state == "infiniteDate" ? "infinity" : state == "unsupportedDate" ? "10000-01-01" : "2026-07-01",
            state == "nan" ? "NaN" : state == "amount" ? "0" : "12.34", state == "currency" ? "USD" : "EUR", null);
        if (state is "matched" or "ignored") factory.Postgres.LinkedTransactions[long.MaxValue] = (null, state);
        if (state == "linked") factory.Postgres.LinkedTransactions[long.MaxValue] = (7, "needsReview");
    }

    private static async Task AssertNoCreationAsync(HttpClient client)
    {
        var expenses = await client.GetFromJsonAsync<JsonElement>("/Expense/latest/100");
        Assert.Empty(expenses.GetProperty("expenses").EnumerateArray());
    }

    [Theory]
    [InlineData("INSERT INTO expenses")]
    [InlineData("UPDATE bank_transactions bt")]
    [InlineData("SELECT bt.id")]
    public async Task CreateAndMatch_ShouldRollBackAllMutations_WhenProviderFailsBeforeCommitAsync(string stage)
    {
        // Arrange
        using var factory = Factory();
        SetProviderFailure(factory, stage);
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(Path, Request());
        factory.Postgres.ExecuteException = null;

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        await AssertNoCreationAsync(client);
        await AssertUnlinkedBankAsync(client);
    }

    private static void SetProviderFailure(CustomWebApplicationFactory factory, string stage)
    {
        var failure = new Amazon.RDSDataService.AmazonRDSDataServiceException("Data API unavailable");
        factory.Postgres.ExecuteException = failure;
        factory.Postgres.ExecuteExceptionSqlPrefix = stage;
    }

    [Theory]
    [InlineData("missing", 404)]
    [InlineData("category", 422)]
    [InlineData("begin", 502)]
    [InlineData("lookup", 502)]
    public async Task CreateAndMatch_ShouldLeaveNoCreation_WhenPrerequisiteFailsAsync(string reason, int status)
    {
        // Arrange
        using var factory = Factory();
        if (reason == "missing") factory.Postgres.LinkedTransactions.Clear();
        if (reason == "category") factory.Postgres.CategoryRows.Clear();
        if (reason == "begin") factory.Postgres.BeginException = new Amazon.RDSDataService.AmazonRDSDataServiceException("Begin unavailable");
        if (reason == "lookup") factory.Postgres.ExecuteException = new Amazon.RDSDataService.AmazonRDSDataServiceException("Lookup unavailable");
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(Path, Request());
        factory.Postgres.ExecuteException = null;

        // Assert
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        await AssertNoCreationAsync(client);
    }
}
