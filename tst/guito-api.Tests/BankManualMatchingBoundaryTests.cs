using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GuitoApi.Tests;

public class BankManualMatchingBoundaryTests
{
    private static CustomWebApplicationFactory Factory()
    {
        var factory = new CustomWebApplicationFactory
        {
            UseEnableBankingProvider = true, UseRealBankTransactionRepository = true
        };
        factory.Postgres.HasBankReviewSchema = true;
        factory.Postgres.NextTransactionId = "manual-match";
        factory.Postgres.LinkedTransactions.Add(long.MaxValue, (null, "needsReview"));
        factory.Postgres.SeedExpense(7, "2026-07-01T12:00:00Z", "12.3400", "Restaurants", description: "Lunch");
        return factory;
    }

    [Fact]
    public async Task Match_ShouldPersistExplicitLink_WhenExpenseIsEligibleAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();

        // Act
        using var matched = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = 7 });

        // Assert
        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);
        var result = await matched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("matched", result.GetProperty("reviewStatus").GetString());
        Assert.Equal(7, result.GetProperty("linkedExpense").GetProperty("id").GetInt32());
        Assert.Equal(0, result.GetProperty("suggestedExpenseCount").GetInt32());
        var persisted = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal(7, persisted.GetProperty("linkedExpense").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Unmatch_ShouldPreserveExpenseAndReturnToReview_WhenExpectedLinkMatchesAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var setup = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = 7 });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

        // Act
        using var response = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/unmatch", new { expenseId = 7 });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("needsReview", result.GetProperty("reviewStatus").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("linkedExpense").ValueKind);
        Assert.Equal(1, result.GetProperty("suggestedExpenseCount").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Expense/7")).StatusCode);
        var persisted = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal("needsReview", persisted.GetProperty("reviewStatus").GetString());
    }

    [Fact]
    public async Task Suggestions_ShouldRankFullHistoryAndExposeCount_WhenMoreThanLatestTenAreEligibleAsync()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.BankDetails[long.MaxValue] = ("2026-07-01", "12.34", "EUR", "Lunch cafe");
        for (var id = 100; id < 120; id++)
            factory.Postgres.SeedExpense(id, "2026-06-29T00:00:00Z", "12.34", "Restaurants", description: "Unrelated");
        factory.Postgres.SeedExpense(10, "2026-06-28T00:00:00Z", "12.34", "Restaurants", description: "LUNCH cafe!");
        factory.Postgres.SeedExpense(9, "2026-07-01T13:00:00Z", "12.34", "Restaurants", description: "Unrelated");
        factory.Postgres.SeedExpense(8, "2026-07-01T13:00:00Z", "12.34", "Restaurants", description: "Unrelated");
        using var client = factory.CreateClient();

        // Act
        var rows = (await client.GetFromJsonAsync<JsonElement[]>($"/BankTransaction/{long.MaxValue}/suggestions"))!;
        var bank = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);

        // Assert
        Assert.Equal(24, rows.Length);
        Assert.Equal(new[] { 10, 7, 9, 8 }, rows.Take(4).Select(row => row.GetProperty("id").GetInt32()));
        Assert.Equal(Enumerable.Range(100, 20).Reverse(), rows.Skip(4).Select(row => row.GetProperty("id").GetInt32()));
        Assert.Equal(24, bank.GetProperty("suggestedExpenseCount").GetInt32());
    }

    [Theory]
    [InlineData("2026-06-28T00:00:00Z", "12.34", "EUR", true)]
    [InlineData("2026-07-04T22:59:59Z", "12.34", "EUR", true)]
    [InlineData("2026-06-27T23:30:00Z", "12.34", "EUR", true)]
    [InlineData("2026-07-04T23:00:00Z", "12.34", "EUR", false)]
    [InlineData("2026-06-27T22:59:59Z", "12.34", "EUR", false)]
    [InlineData("2026-07-01T00:00:00Z", "12.340000000000000000000000001", "EUR", false)]
    [InlineData("2026-07-01T00:00:00Z", "12.35", "EUR", false)]
    [InlineData("2026-07-01T00:00:00Z", "-12.34", "EUR", false)]
    [InlineData("2026-07-01T00:00:00Z", "0", "EUR", false)]
    [InlineData("2026-07-01T00:00:00Z", "12.34", "USD", false)]
    public async Task Match_ShouldRevalidateExactEligibility_WhenCandidateDateAmountOrCurrencyDiffersAsync(string instant, string amount, string currency, bool eligible)
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.SeedExpense(8, instant, amount, "Groceries", description: "Completely different merchant");
        factory.Postgres.BankDetails[long.MaxValue] = ("2026-07-01", "12.34", currency, "Bank description");
        using var client = factory.CreateClient();
        var candidates = (await client.GetFromJsonAsync<JsonElement[]>($"/BankTransaction/{long.MaxValue}/suggestions"))!;

        // Act
        using var response = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = 8 });

        // Assert
        Assert.Equal(eligible, candidates.Any(row => row.GetProperty("id").GetInt32() == 8));
        Assert.Equal(eligible ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
        var bank = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal(eligible ? "matched" : "needsReview", bank.GetProperty("reviewStatus").GetString());
        Assert.Equal(eligible ? 0 : 1, factory.Postgres.Rollbacks);
    }

    [Theory]
    [InlineData("2026-03-29", "2026-04-01T22:59:59Z", true)]
    [InlineData("2026-03-29", "2026-04-01T23:00:00Z", false)]
    [InlineData("2026-10-25", "2026-10-21T23:30:00Z", true)]
    [InlineData("2026-10-25", "2026-10-21T22:59:59Z", false)]
    public async Task Suggestions_ShouldUseLisbonCalendarDays_WhenWindowCrossesDstAsync(string day, string instant, bool eligible)
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.BankDetails[long.MaxValue] = (day, "12.34", "EUR", null);
        factory.Postgres.SeedExpense(8, instant, "12.34", "Restaurants");
        using var client = factory.CreateClient();

        // Act
        var candidates = (await client.GetFromJsonAsync<JsonElement[]>($"/BankTransaction/{long.MaxValue}/suggestions"))!;

        // Assert
        Assert.Equal(eligible, candidates.Any(row => row.GetProperty("id").GetInt32() == 8));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("9223372036854775808")]
    public async Task Request_ShouldRejectInvalidTransactionIds_BeforeTransportAsync(string id)
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();

        // Act
        using var suggestions = await client.GetAsync($"/BankTransaction/{id}/suggestions");
        using var match = await client.PostAsJsonAsync($"/BankTransaction/{id}/match", new { expenseId = 7 });
        using var unmatch = await client.PostAsJsonAsync($"/BankTransaction/{id}/unmatch", new { expenseId = 7 });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, suggestions.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, match.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unmatch.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"expenseId\":null}")]
    [InlineData("{\"expenseId\":0}")]
    [InlineData("{\"expenseId\":-1}")]
    [InlineData("{\"expenseId\":2147483648}")]
    [InlineData("{\"expenseId\":1.5}")]
    [InlineData("{\"expenseId\":\"7\"}")]
    public async Task Post_ShouldRejectInvalidExpenseIdBody_BeforeTransportAsync(string json)
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();

        // Act
        using var match = await client.PostAsync($"/BankTransaction/{long.MaxValue}/match", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        using var unmatch = await client.PostAsync($"/BankTransaction/{long.MaxValue}/unmatch", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, match.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unmatch.StatusCode);
        Assert.Empty(factory.Postgres.ExecutedSql);
    }

    [Theory]
    [InlineData("match")]
    [InlineData("unmatch")]
    public async Task Post_ShouldReturnNotFound_WhenExactExpenseOrTransactionIsMissingAsync(string action)
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();

        // Act
        using var bank = await client.PostAsJsonAsync($"/BankTransaction/99/{action}", new { expenseId = 7 });
        using var expense = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/{action}", new { expenseId = 999 });
        using var suggestions = await client.GetAsync("/BankTransaction/99/suggestions");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, bank.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, expense.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, suggestions.StatusCode);
        Assert.Equal(0, factory.Postgres.Commits);
    }

    [Theory]
    [InlineData("matched", 7)]
    [InlineData("ignored", null)]
    public async Task Match_ShouldRejectStaleState_WithoutChangingExistingDecisionAsync(string state, int? link)
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.LinkedTransactions[long.MaxValue] = (link, state);
        factory.Postgres.SeedExpense(8, "2026-07-01T00:00:00Z", "12.34", "Restaurants");
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = 8 });
        var suggestions = await client.GetFromJsonAsync<JsonElement[]>($"/BankTransaction/{long.MaxValue}/suggestions");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(suggestions!);
        var bank = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal(state, bank.GetProperty("reviewStatus").GetString());
        Assert.Equal(0, bank.GetProperty("suggestedExpenseCount").GetInt32());
    }

    [Fact]
    public async Task Match_ShouldExcludeAlreadyLinkedExpense_AndRejectSecondTransactionAsync()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.LinkedTransactions[10] = (7, "matched");
        using var client = factory.CreateClient();

        // Act
        var suggestions = await client.GetFromJsonAsync<JsonElement[]>($"/BankTransaction/{long.MaxValue}/suggestions");
        using var response = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = 7 });

        // Assert
        Assert.Empty(suggestions!);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var rows = (await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!;
        Assert.Equal(7, rows.Single(row => row.GetProperty("id").GetInt64() == 10).GetProperty("linkedExpense").GetProperty("id").GetInt32());
        Assert.Equal("needsReview", rows.Single(row => row.GetProperty("id").GetInt64() == long.MaxValue).GetProperty("reviewStatus").GetString());
    }

    [Fact]
    public async Task Unmatch_ShouldRejectOldTabExpectedExpense_WithoutRemovingNewLinkAsync()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.SeedExpense(8, "2026-07-01T00:00:00Z", "12.34", "Restaurants");
        factory.Postgres.LinkedTransactions[long.MaxValue] = (8, "matched");
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/unmatch", new { expenseId = 7 });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var bank = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal(8, bank.GetProperty("linkedExpense").GetProperty("id").GetInt32());
        Assert.Equal(1, factory.Postgres.Rollbacks);
    }

    [Theory]
    [InlineData("match", 1)]
    [InlineData("match", 3)]
    [InlineData("match", 4)]
    [InlineData("unmatch", 1)]
    [InlineData("unmatch", 3)]
    [InlineData("unmatch", 4)]
    public async Task Post_ShouldRollBackOnProviderFailure_WhenWriteOrReadbackFailsAsync(string action, int call)
    {
        // Arrange
        using var factory = Factory();
        if (action == "unmatch") factory.Postgres.LinkedTransactions[long.MaxValue] = (7, "matched");
        factory.Postgres.ExecuteExceptionAtCall = call;
        factory.Postgres.ExecuteException = new Amazon.RDSDataService.AmazonRDSDataServiceException("provider response limit");
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/{action}", new { expenseId = 7 });
        factory.Postgres.ExecuteException = null;
        var bank = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(action == "match" ? "needsReview" : "matched", bank.GetProperty("reviewStatus").GetString());
        Assert.Equal(0, factory.Postgres.Commits);
        Assert.Equal(1, factory.Postgres.Rollbacks);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Expense/7")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_ShouldReturnProviderFailure_WhenTransactionLifecycleFailsAsync(bool commit)
    {
        // Arrange
        using var factory = Factory();
        var failure = new Amazon.RDSDataService.AmazonRDSDataServiceException("provider unavailable");
        if (commit) factory.Postgres.CommitException = failure;
        else factory.Postgres.BeginException = failure;
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = 7 });

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Edit_ShouldRetainConfirmedMatch_WhenAmountAndDateChangeAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var setup = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = 7 });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/Expense/7")
        {
            Content = JsonContent.Create(new { description = "Edited", category = "Groceries", amount = "19.99", bookingDate = "2026-07-19T10:00:00+01:00" })
        };
        request.Headers.Add("If-Match", "\"1\"");

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var linked = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!).GetProperty("linkedExpense");
        Assert.Equal(7, linked.GetProperty("id").GetInt32());
        Assert.Equal("19.99", linked.GetProperty("amount").GetString());
        Assert.Equal("Edited", linked.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Delete_ShouldClearNewMatchAndRetainOtherBankDecisions_WhenExpenseIsDeletedAsync()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.LinkedTransactions[10] = (null, "ignored");
        using var client = factory.CreateClient();
        using var setup = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = 7 });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/Expense/7");
        request.Headers.Add("If-Match", "\"1\"");

        // Act
        using var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var rows = (await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!;
        var bank = rows.Single(row => row.GetProperty("id").GetInt64() == long.MaxValue);
        Assert.Equal("needsReview", bank.GetProperty("reviewStatus").GetString());
        Assert.Equal(JsonValueKind.Null, bank.GetProperty("linkedExpense").ValueKind);
        Assert.Equal(0, bank.GetProperty("suggestedExpenseCount").GetInt32());
        Assert.Equal("ignored", rows.Single(row => row.GetProperty("id").GetInt64() == 10).GetProperty("reviewStatus").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Expense/7")).StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Suggestions_ShouldFailWholeResponse_WhenProviderCannotReturnCompleteDataAsync(int call)
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.ExecuteExceptionAtCall = call;
        factory.Postgres.ExecuteException = new Amazon.RDSDataService.AmazonRDSDataServiceException("response limit");
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync($"/BankTransaction/{long.MaxValue}/suggestions");

        // Assert
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Theory]
    [InlineData("match")]
    [InlineData("ignore")]
    [InlineData("review")]
    public async Task Post_ShouldRejectRepeatedOrIncompatibleAction_WhenTransactionIsMatchedAsync(string action)
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var setup = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = 7 });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);

        // Act
        using var response = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/{action}", new { expenseId = 7 });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var bank = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal(7, bank.GetProperty("linkedExpense").GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("ignored", 0)]
    [InlineData("needsReview", 1)]
    public async Task Unmatch_ShouldRejectAbsentLink_WhenTransactionIsUnlinkedAsync(string state, int count)
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.LinkedTransactions[long.MaxValue] = (null, state);
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/unmatch", new { expenseId = 7 });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var bank = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal(state, bank.GetProperty("reviewStatus").GetString());
        Assert.Equal(count, bank.GetProperty("suggestedExpenseCount").GetInt32());
    }

    [Theory]
    [InlineData("ignore", "ignored", 0)]
    [InlineData("review", "needsReview", 1)]
    public async Task Post_ShouldIncludeUpdatedCandidateCount_WhenReviewActionReturnsTransactionAsync(string action, string state, int count)
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.LinkedTransactions[long.MaxValue] = (null, action == "review" ? "ignored" : "needsReview");
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsync($"/BankTransaction/{long.MaxValue}/{action}", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bank = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(state, bank.GetProperty("reviewStatus").GetString());
        Assert.Equal(count, bank.GetProperty("suggestedExpenseCount").GetInt32());
    }

    [Fact]
    public async Task Suggestions_ShouldDistinguishLargeExactAmounts_WhenDoubleWouldCollapseThemAsync()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.BankDetails[long.MaxValue] = ("2026-07-01", "9999999999999999.99", "EUR", null);
        factory.Postgres.SeedExpense(int.MaxValue, "2026-07-01T00:00:00Z", "9999999999999999.9900", "Restaurants");
        factory.Postgres.SeedExpense(8, "2026-07-01T00:00:00Z", "9999999999999999.98", "Restaurants");
        using var client = factory.CreateClient();

        // Act
        var suggestions = (await client.GetFromJsonAsync<JsonElement[]>($"/BankTransaction/{long.MaxValue}/suggestions"))!;
        using var response = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = int.MaxValue });

        // Assert
        var candidate = Assert.Single(suggestions);
        Assert.Equal(int.MaxValue, candidate.GetProperty("id").GetInt32());
        Assert.Equal("9999999999999999.99", candidate.GetProperty("amount").GetString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bank = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("9999999999999999.99", bank.GetProperty("linkedExpense").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Match_ShouldRejectExistingLink_EvenWhenReviewStateIsIncoherentAsync()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.LinkedTransactions[long.MaxValue] = (7, "needsReview");
        factory.Postgres.SeedExpense(8, "2026-07-01T00:00:00Z", "12.34", "Restaurants");
        using var client = factory.CreateClient();

        // Act
        using var response = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = 8 });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, factory.Postgres.Rollbacks);
        Assert.Equal(0, factory.Postgres.Commits);
    }

    [Fact]
    public async Task Match_ShouldRejectEditedExpense_WhenUnmatchedExpenseNoLongerQualifiesAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        await ArrangeEditedMatchAsync(client);
        using var unmatch = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/unmatch", new { expenseId = 7 });
        Assert.Equal(HttpStatusCode.OK, unmatch.StatusCode);
        var unlinked = await unmatch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, unlinked.GetProperty("suggestedExpenseCount").GetInt32());

        // Act
        using var response = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = 7 });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var bank = Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction"))!);
        Assert.Equal("needsReview", bank.GetProperty("reviewStatus").GetString());
        Assert.Equal(JsonValueKind.Null, bank.GetProperty("linkedExpense").ValueKind);
        var expense = await client.GetFromJsonAsync<JsonElement>("/Expense/7");
        Assert.Equal("19.99", expense.GetProperty("amount").GetString());
        Assert.Equal("Edited", expense.GetProperty("description").GetString());
    }

    private static async Task ArrangeEditedMatchAsync(HttpClient client)
    {
        using var matched = await client.PostAsJsonAsync($"/BankTransaction/{long.MaxValue}/match", new { expenseId = 7 });
        Assert.Equal(HttpStatusCode.OK, matched.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/Expense/7")
        {
            Content = JsonContent.Create(new { description = "Edited", category = "Groceries", amount = "19.99", bookingDate = "2026-07-19T10:00:00+01:00" })
        };
        request.Headers.Add("If-Match", "\"1\"");
        using var edited = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
    }

    [Fact]
    public async Task Suggestions_ShouldReturnEligibleExpenseWithExactWireShape_WhenPersistedTransactionExistsAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync($"/BankTransaction/{long.MaxValue}/suggestions");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expense = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement[]>())!);
        Assert.Equal(7, expense.GetProperty("id").GetInt32());
        Assert.Equal("12.34", expense.GetProperty("amount").GetString());
        Assert.Equal("EUR", expense.GetProperty("currency").GetString());
        Assert.Equal("2026-07-01T13:00:00+01:00", expense.GetProperty("bookingDate").GetString());
        Assert.Equal("Lunch", expense.GetProperty("description").GetString());
        Assert.Equal("Restaurants", expense.GetProperty("category").GetString());
    }
}
