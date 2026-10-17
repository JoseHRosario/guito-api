using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GuitoApi.Tests;

public class BankCreateAndMatchValidationTests
{
    private static string Path(long id = long.MaxValue) => $"/BankTransaction/{id}/create-and-match";

    private static CustomWebApplicationFactory Factory()
    {
        var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true, UseRealBankTransactionRepository = true };
        factory.Postgres.HasBankReviewSchema = true;
        factory.Postgres.NextTransactionId = "validation-create-match";
        factory.Postgres.LinkedTransactions.Add(long.MaxValue, (null, "needsReview"));
        return factory;
    }

    private static JsonObject Request() => new()
    {
        ["bookingDate"] = "2026-07-01", ["amount"] = "9007199254740993.123456789",
        ["description"] = "Exact purchase", ["category"] = "Groceries", ["currency"] = "EUR"
    };

    [Theory]
    [InlineData("bookingDate", "missing")]
    [InlineData("bookingDate", null)]
    [InlineData("bookingDate", "2026-07-01T12:00:00")]
    [InlineData("bookingDate", "2026-03-29T01:30:00")]
    [InlineData("bookingDate", "2026-10-25T01:30:00")]
    [InlineData("bookingDate", "2026-07-01T12:00:00.1234567Z")]
    [InlineData("amount", "1.00000000000000000000000000001")]
    [InlineData("currency", "USD")]
    [InlineData("date", null)]
    [InlineData("DaTe", "2026-07-01")]
    [InlineData("occurredAt", null)]
    [InlineData("OCCURREDAT", "2026-07-01T00:00:00Z")]
    public async Task CreateAndMatch_ShouldRejectMalformedExpense_WhenBodyIsInvalidAsync(string field, string? value)
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        var request = Request();
        if (value == "missing") request.Remove(field);
        else request[field] = value;

        // Act
        using var response = await client.PostAsJsonAsync(Path(), request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var expenses = await client.GetFromJsonAsync<JsonElement>("/Expense/latest/100");
        Assert.Empty(expenses.GetProperty("expenses").EnumerateArray());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9223372036854775808")]
    [InlineData("not-a-number")]
    [InlineData("1.5")]
    public async Task CreateAndMatch_ShouldRejectMalformedId_WhenRouteIsCalledAsync(string id)
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        var path = $"/BankTransaction/{id}/create-and-match";

        // Act
        using var post = await client.PostAsJsonAsync(path, Request());

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAndMatch_ShouldNormalizeLisbonMidnightAndExactMoney_WhenDateOnlyIsUsedAsync(bool numericAmount)
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        var request = Request();
        if (numericAmount) request["amount"] = 9007199254740993.123456789m;

        // Act
        using var response = await client.PostAsJsonAsync(Path(), request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expense = await client.GetFromJsonAsync<JsonElement>("/Expense/1");
        Assert.Equal("9007199254740993.123456789", expense.GetProperty("amount").GetString());
        Assert.Equal("2026-07-01T00:00:00+01:00", expense.GetProperty("bookingDate").GetString());
        Assert.Equal("xunga.meireles@gmail.com", expense.GetProperty("creatorEmail").GetString());
        Assert.Equal(JsonValueKind.Null, expense.GetProperty("updateDate").ValueKind);
    }
}
