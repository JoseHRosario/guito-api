using System.Net;
using GuitoApi.Infrastructure.Postgres;

namespace GuitoApi.Tests;

public class ExpenseUnsupportedLinkSchemaTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("integer", null)]
    [InlineData("text", "text")]
    [InlineData("bigint", "text")]
    [InlineData("integer", "boolean")]
    [InlineData(null, "text")]
    public async Task Delete_ShouldRollbackWithoutChangingExpenseOrLinks_WhenLinkMetadataIsUnsupportedAsync(string? linkType, string? reviewType)
    {
        // Arrange
        using var factory = CreateFactory(linkType, reviewType);
        using var client = factory.CreateClient();
        var before = await client.GetStringAsync("/Expense/7");
        var links = factory.Postgres.LinkedTransactions.ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/Expense/7");
        request.Headers.Add("If-Match", "\"1\"");
        // Act
        using var response = await client.SendAsync(request);
        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertRollbackAsync(factory, client, before, links);
    }

    private static CustomWebApplicationFactory CreateFactory(string? linkType, string? reviewType)
    {
        var factory = new CustomWebApplicationFactory { ExpenseStore = "Postgres", Postgres = { PersistExpenses = true } };
        factory.Postgres.NextTransactionId = "unsupported-schema";
        factory.Postgres.SeedExpense(7, "2026-07-02T13:23:45Z", "12.3400", "Groceries");
        factory.Postgres.LinkSchemaColumns = [];
        if (linkType is not null) factory.Postgres.LinkSchemaColumns.Add("expense_id", linkType);
        if (reviewType is not null) factory.Postgres.LinkSchemaColumns.Add("review_status", reviewType);
        factory.Postgres.LinkedTransactions.Add(100, (7, "matched"));
        factory.Postgres.LinkedTransactions.Add(101, (8, "matched"));
        factory.Postgres.LinkedTransactions.Add(102, (null, "ignored"));
        return factory;
    }

    private static async Task AssertRollbackAsync(CustomWebApplicationFactory factory, HttpClient client, string before,
        KeyValuePair<long, (int? ExpenseId, string Status)>[] links)
    {
        Assert.Equal(before, await client.GetStringAsync("/Expense/7"));
        Assert.Equal(links, factory.Postgres.LinkedTransactions.ToArray());
        Assert.Equal(0, factory.Postgres.Commits);
        Assert.Equal(1, factory.Postgres.Rollbacks);
        Assert.DoesNotContain(factory.Postgres.ExecutedSql, sql => sql.StartsWith("UPDATE bank_transactions", StringComparison.Ordinal));
        Assert.DoesNotContain(factory.Postgres.ExecutedSql, sql => sql.StartsWith("DELETE FROM expenses", StringComparison.Ordinal));
    }
}
