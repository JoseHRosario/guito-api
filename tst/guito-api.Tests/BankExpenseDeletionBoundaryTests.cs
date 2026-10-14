using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GuitoApi.Tests;

public class BankExpenseDeletionBoundaryTests
{
    [Fact]
    public async Task Delete_ShouldReturnLinkedTransactionToReview_WhenExpenseAndBankRepositoriesSharePostgresAsync()
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        await AssertOriginalLinkAsync(client);
        using var request = DeleteRequest();

        // Act
        var response = await client.SendAsync(request);
        var after = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await AssertExpenseExistenceAsync(client);
        AssertRetainedTransactions(after!);
    }

    private static CustomWebApplicationFactory CreateFactory()
    {
        var factory = new CustomWebApplicationFactory
        {
            UseEnableBankingProvider = true,
            UseRealBankTransactionRepository = true
        };
        factory.Postgres.NextTransactionId = "bank-expense-delete";
        factory.Postgres.HasBankReviewSchema = true;
        factory.Postgres.SeedExpense(7, "2026-07-01T12:34:56.123456Z", "12.3400", "Restaurants");
        factory.Postgres.SeedExpense(8, "2026-07-02T12:00:00Z", "5.00", "Groceries");
        factory.Postgres.LinkedTransactions.Add(10, (7, "matched"));
        factory.Postgres.LinkedTransactions.Add(11, (8, "matched"));
        factory.Postgres.LinkedTransactions.Add(12, (null, "ignored"));
        return factory;
    }

    private static async Task AssertOriginalLinkAsync(HttpClient client)
    {
        var before = await client.GetFromJsonAsync<JsonElement[]>("/BankTransaction");
        var linked = before!.Single(row => row.GetProperty("id").GetInt64() == 10).GetProperty("linkedExpense");
        Assert.Equal(7, linked.GetProperty("id").GetInt32());
        Assert.Equal("12.34", linked.GetProperty("amount").GetString());
        Assert.Equal("2026-07-01T13:34:56.123456+01:00", linked.GetProperty("bookingDate").GetString());
    }

    private static async Task AssertExpenseExistenceAsync(HttpClient client)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Expense/7")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Expense/8")).StatusCode);
    }

    private static void AssertRetainedTransactions(JsonElement[] after)
    {
        Assert.Equal(3, after.Length);
        var returned = after.Single(row => row.GetProperty("id").GetInt64() == 10);
        Assert.Equal("needsReview", returned.GetProperty("reviewStatus").GetString());
        Assert.Equal(JsonValueKind.Null, returned.GetProperty("linkedExpense").ValueKind);
        Assert.Equal("matched", after.Single(row => row.GetProperty("id").GetInt64() == 11).GetProperty("reviewStatus").GetString());
        Assert.Equal("ignored", after.Single(row => row.GetProperty("id").GetInt64() == 12).GetProperty("reviewStatus").GetString());
    }

    [Fact]
    public async Task Ignore_ShouldSucceed_WhenLinkedExpenseWasDeletedAsync()
    {
        // Arrange
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var deletion = DeleteRequest();
        using var deleted = await client.SendAsync(deletion);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // Act
        using var ignored = await client.PostAsync("/BankTransaction/10/ignore", null);
        var transaction = await ignored.Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        Assert.Equal(HttpStatusCode.OK, ignored.StatusCode);
        Assert.Equal("ignored", transaction.GetProperty("reviewStatus").GetString());
    }

    private static HttpRequestMessage DeleteRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/Expense/7");
        request.Headers.Add("If-Match", "\"1\"");
        return request;
    }
}
