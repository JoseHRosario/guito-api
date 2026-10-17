using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GuitoApi.Tests;

public class BankCreateAndMatchValidationTests
{
    private const string OperationId = "11111111-2222-3333-4444-555555555555";
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
        ["operationId"] = OperationId, ["bookingDate"] = "2026-07-01", ["amount"] = "9007199254740993.123456789",
        ["description"] = "Exact purchase", ["category"] = "Groceries", ["currency"] = "EUR"
    };

    [Theory]
    [InlineData("operationId", null)]
    [InlineData("operationId", "missing")]
    [InlineData("operationId", "invalid")]
    [InlineData("operationId", "00000000-0000-0000-0000-000000000000")]
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
    public async Task CreateAndMatch_ShouldRejectMalformedExpenseOrOperation_WhenBodyIsInvalidAsync(string field, string? value)
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
    public async Task CreateAndMatch_ShouldRejectMalformedId_WhenEitherRouteIsCalledAsync(string id)
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        var path = $"/BankTransaction/{id}/create-and-match";

        // Act
        using var post = await client.PostAsJsonAsync(path, Request());
        using var get = await client.GetAsync(path + "/" + OperationId);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task CreationReceipt_ShouldRejectMalformedUuid_WhenRouteIsCalledAsync(string operationId)
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();

        // Act
        using var response = await client.GetAsync(Path() + "/" + operationId);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("amount", "9007199254740993.123456788")]
    [InlineData("bookingDate", "2026-07-02")]
    [InlineData("description", "exact purchase")]
    [InlineData("description", "Exact purchase ")]
    [InlineData("category", "Restaurants")]
    [InlineData("category", "Groceries ")]
    public async Task CreateAndMatch_ShouldRejectChangedIntent_WhenOperationIdWasCommittedAsync(string field, string value)
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync(Path(), Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var changed = Request();
        changed[field] = value;

        // Act
        using var response = await client.PostAsJsonAsync(Path(), changed);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var expenses = await client.GetFromJsonAsync<JsonElement>("/Expense/latest/100");
        Assert.Single(expenses.GetProperty("expenses").EnumerateArray());
    }

    [Fact]
    public async Task CreateAndMatch_ShouldNormalizeLisbonMidnightAndExactMoney_WhenDateOnlyAndNumericRetryAreUsedAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        var request = Request();
        request["unrelatedExtension"] = "ignored as for normal create";
        using var first = await client.PostAsJsonAsync(Path(), request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var retryRequest = Request();
        retryRequest["bookingDate"] = "2026-06-30T23:00:00Z";
        retryRequest["amount"] = 9007199254740993.123456789m;

        // Act
        using var retry = await client.PostAsJsonAsync(Path(), retryRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        var expense = await client.GetFromJsonAsync<JsonElement>("/Expense/1");
        Assert.Equal("9007199254740993.123456789", expense.GetProperty("amount").GetString());
        Assert.Equal("2026-07-01T00:00:00+01:00", expense.GetProperty("bookingDate").GetString());
        Assert.Equal("xunga.meireles@gmail.com", expense.GetProperty("creatorEmail").GetString());
        Assert.Equal(JsonValueKind.Null, expense.GetProperty("updateDate").ValueKind);
    }

    [Fact]
    public async Task CreationReceipt_ShouldRequireExactPair_WhenOperationBelongsToAnotherBankAsync()
    {
        // Arrange
        using var factory = Factory();
        factory.Postgres.LinkedTransactions[8] = (null, "needsReview");
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync(Path(), Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Act
        using var otherBank = await client.PostAsJsonAsync(Path(8), Request());
        using var wrongPair = await client.GetAsync(Path(8) + "/" + OperationId);
        using var unknown = await client.GetAsync(Path() + "/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, otherBank.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongPair.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.True(wrongPair.Headers.CacheControl?.NoStore);
        Assert.True(unknown.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("eur")]
    public async Task CreateAndMatch_ShouldPreserveExpenseValidation_WhenCommittedIntentIsRetriedWithInvalidCurrencyAsync(string currency)
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync(Path(), Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var changed = Request();
        changed["currency"] = currency;

        // Act
        using var response = await client.PostAsJsonAsync(Path(), changed);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var expenses = await client.GetFromJsonAsync<JsonElement>("/Expense/latest/100");
        Assert.Single(expenses.GetProperty("expenses").EnumerateArray());
    }

    [Fact]
    public async Task CreateAndMatch_ShouldRejectNewOperation_WhenBankWasAlreadyMatchedAsync()
    {
        // Arrange
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync(Path(), Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var request = Request();
        request["operationId"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        // Act
        using var response = await client.PostAsJsonAsync(Path(), request);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var expenses = await client.GetFromJsonAsync<JsonElement>("/Expense/latest/100");
        Assert.Single(expenses.GetProperty("expenses").EnumerateArray());
    }
}
