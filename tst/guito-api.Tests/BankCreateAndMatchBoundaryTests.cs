using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GuitoApi.Tests;

public class BankCreateAndMatchBoundaryTests
{
    private const string Path = "/BankTransaction/9223372036854775807/create-and-match";
    private const string ReceiptPath = Path + "/11111111-2222-3333-4444-555555555555";

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
        operationId = "11111111-2222-3333-4444-555555555555", amount, bookingDate = date,
        description = "Edited purchase", category = "Groceries", currency = "EUR"
    };

    [Fact]
    public async Task CreateAndMatch_ShouldPersistEditedExpenseAndReceipt_WhenSaveIsExplicitAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(Path, Request());

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(long.MaxValue, receipt.GetProperty("transactionId").GetInt64());
        Assert.Equal(1, receipt.GetProperty("expenseId").GetInt32());
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

    [Fact]
    public async Task CreateAndMatch_ShouldReturnOriginalReceipt_WhenNormalizedIntentIsRetriedAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync(Path, Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Act
        using var retry = await client.PostAsJsonAsync(Path, Request("-98.765432100", "2026-08-20T21:45:12.123456Z"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        await AssertReceiptAsync(client, await first.Content.ReadAsStringAsync());
        await AssertSingleMatchedExpenseAsync(client);
    }

    [Fact]
    public async Task CreationReceipt_ShouldPreserveOriginalEvidence_WhenExpenseIsUnmatchedAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync(Path, Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Act
        using var unmatch = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/unmatch", new { expenseId = 1 });

        // Assert
        Assert.Equal(HttpStatusCode.OK, unmatch.StatusCode);
        await AssertReceiptAsync(client, await first.Content.ReadAsStringAsync());
        await AssertUnlinkedBankAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Expense/1")).StatusCode);
    }

    [Fact]
    public async Task CreateAndMatch_ShouldNotRelinkExpense_WhenCommittedOperationIsRetriedAfterUnmatchAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync(Path, Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var unmatch = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/unmatch", new { expenseId = 1 });
        Assert.Equal(HttpStatusCode.OK, unmatch.StatusCode);

        // Act
        using var retry = await client.PostAsJsonAsync(Path, Request());

        // Assert
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        await AssertUnlinkedBankAsync(client);
    }

    [Fact]
    public async Task CreationReceipt_ShouldPreserveOriginalEvidence_WhenMatchedExpenseIsDeletedAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync(Path, Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var delete = DeleteRequest();

        // Act
        using var deleted = await client.SendAsync(delete);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await AssertReceiptAsync(client, await first.Content.ReadAsStringAsync());
        await AssertUnlinkedBankAsync(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Expense/1")).StatusCode);
    }

    [Fact]
    public async Task CreateAndMatch_ShouldNotRecreateExpense_WhenCommittedOperationIsRetriedAfterDeletionAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync(Path, Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var delete = DeleteRequest();
        using var deleted = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // Act
        using var retry = await client.PostAsJsonAsync(Path, Request());

        // Assert
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        await AssertUnlinkedBankAsync(client);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Expense/1")).StatusCode);
    }

    private static HttpRequestMessage DeleteRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/Expense/1");
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        return request;
    }

    private static async Task AssertReceiptAsync(HttpClient client, string original)
    {
        using var receipt = await client.GetAsync(ReceiptPath);
        Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
        Assert.Equal(original, await receipt.Content.ReadAsStringAsync());
        Assert.True(receipt.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task CreationReceipt_ShouldRecoverDurableEvidence_WhenCommitAppliesButResponseTimesOutAsync()
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
        await AssertReceiptAsync(client, "{\"operationId\":\"11111111-2222-3333-4444-555555555555\",\"transactionId\":9223372036854775807,\"expenseId\":1}");
        await AssertSingleMatchedExpenseAsync(client);
    }

    [Fact]
    public async Task CreateAndMatch_ShouldRecoverOriginalReceipt_WhenAppliedCommitIsRetriedAsync()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.CommitAppliedThenThrow = new TimeoutException("Commit response lost");
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync(Path, Request());
        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);

        // Act
        using var retry = await client.PostAsJsonAsync(Path, Request());

        // Assert
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await AssertReceiptAsync(client, await retry.Content.ReadAsStringAsync());
        await AssertSingleMatchedExpenseAsync(client);
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
        using var receipt = await client.GetAsync(ReceiptPath);
        Assert.Equal(HttpStatusCode.NotFound, receipt.StatusCode);
        Assert.True(receipt.Headers.CacheControl?.NoStore);
        var expenses = await client.GetFromJsonAsync<JsonElement>("/Expense/latest/100");
        Assert.Empty(expenses.GetProperty("expenses").EnumerateArray());
    }

    [Theory]
    [InlineData("INSERT INTO expenses")]
    [InlineData("UPDATE bank_transactions bt")]
    [InlineData("INSERT INTO bank_expense_creation_receipts")]
    [InlineData("after receipt")]
    public async Task CreateAndMatch_ShouldRollBackAllMutations_WhenProviderFailsBeforeCommitAsync(string stage)
    {
        // Arrange
        using var factory = Factory();
        SetProviderFailure(factory, stage);
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(Path, Request());
        factory.Postgres.ExecuteException = null;
        factory.Postgres.ReceiptInsertedThenThrow = null;

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        await AssertNoCreationAsync(client);
        await AssertUnlinkedBankAsync(client);
    }

    private static void SetProviderFailure(CustomWebApplicationFactory factory, string stage)
    {
        var failure = new Amazon.RDSDataService.AmazonRDSDataServiceException("Data API unavailable");
        if (stage == "after receipt") factory.Postgres.ReceiptInsertedThenThrow = failure;
        else
        {
            factory.Postgres.ExecuteException = failure;
            factory.Postgres.ExecuteExceptionSqlPrefix = stage;
        }
    }

    [Theory]
    [InlineData("INSERT INTO expenses")]
    [InlineData("UPDATE bank_transactions bt")]
    [InlineData("INSERT INTO bank_expense_creation_receipts")]
    [InlineData("after receipt")]
    public async Task CreateAndMatch_ShouldAllowIdenticalRetry_WhenPreviousAttemptRolledBackAsync(string stage)
    {
        // Arrange
        using var factory = Factory();
        SetProviderFailure(factory, stage);
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync(Path, Request());
        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        factory.Postgres.ExecuteException = null;
        factory.Postgres.ReceiptInsertedThenThrow = null;

        // Act
        using var retry = await client.PostAsJsonAsync(Path, Request());

        // Assert
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await AssertSingleMatchedExpenseAsync(client);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAndMatch_ShouldRecoverWinnerAfterRollback_WhenUuidInsertionConflictsAsync(bool differentIntent)
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.SeedExpense(7, "2026-01-01T00:00:00Z", "5", "Groceries");
        factory.Postgres.CompetingReceipt = (long.MaxValue, 7, differentIntent);
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync(Path, Request());

        // Assert
        Assert.Equal(differentIntent ? HttpStatusCode.Conflict : HttpStatusCode.OK, response.StatusCode);
        await AssertRecoveredWinnerAsync(client);
    }

    private static async Task AssertRecoveredWinnerAsync(HttpClient client)
    {
        using var read = await client.GetAsync(ReceiptPath);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(7, (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("expenseId").GetInt32());
        var expenses = await client.GetFromJsonAsync<JsonElement>("/Expense/latest/100");
        Assert.Equal(7, Assert.Single(expenses.GetProperty("expenses").EnumerateArray()).GetProperty("id").GetInt32());
        await AssertUnlinkedBankAsync(client);
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
