using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.Model;
using GuitoApi.Infrastructure.Postgres;

namespace GuitoApi.Tests;

/// <summary>
/// Preserved Bank wire fields through the real service/repository composition
/// with scripted Data API responses. Stateful filters/order/actions are covered
/// by BankTransactionReviewBoundaryTests.
/// </summary>
public class BankTransactionBoundaryTests
{
    private static CustomWebApplicationFactory FactoryWithPending(params BankTransactionDetail[] pending)
    {
        var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true, UseRealBankTransactionRepository = true };
        var rows = pending.Select(row => (IReadOnlyList<PostgresValue>)new PostgresValue[]
        {
            PostgresValue.FromLong(row.Id), PostgresValue.FromString(row.AccountUid),
            PostgresValue.FromString(row.BookingDate.ToString("yyyy-MM-dd")),
            PostgresValue.FromString(row.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            PostgresValue.FromString(row.Currency), row.RemittanceInformation is { } description
                ? PostgresValue.FromString(description) : PostgresValue.Null(),
            row.SuggestedCategory is { } category ? PostgresValue.FromLong(category.Id) : PostgresValue.Null(),
            row.SuggestedCategory is { } suggestion ? PostgresValue.FromString(suggestion.Name) : PostgresValue.Null(),
            PostgresValue.FromString("BOOK"), PostgresValue.FromString("needsReview"),
            PostgresValue.Null(), PostgresValue.Null(), PostgresValue.Null(), PostgresValue.Null(), PostgresValue.Null()
        }).ToList();
        factory.Postgres.NextResults.Enqueue(new PostgresResult([], rows, 0));
        return factory;
    }

    private static BankTransactionDetail PendingRow(
        long id = 7,
        string accountUid = "eb-account-1",
        string bookingDate = "2026-10-05",
        decimal amount = 2.30m,
        string remittance = "COMPRA 9166 MEO") => new(
        Id: id,
        AccountUid: accountUid,
        BookingDate: DateOnly.Parse(bookingDate),
        Amount: amount,
        Currency: "EUR",
        RemittanceInformation: remittance);

    [Fact]
    public async Task Get_ShouldReturnPendingRows_AsRepositoryOrdersThem_WhenUnmatchedTransactionsExist()
    {
        // Arrange
        using var factory = FactoryWithPending(
            PendingRow(id: 9, bookingDate: "2026-10-05", amount: 12.50m, remittance: "PINGO DOCE"),
            PendingRow(id: 4, bookingDate: "2026-10-02", amount: 77.93m, remittance: "COMPRA 9166 MEO"));
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/banktransaction");

        // Assert — the repository orders rows (newest bookings first); the
        // endpoint must pass them through untouched, one wire DTO per row.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
        Assert.Equal(2, rows.Length);
        Assert.Equal(9, rows[0].GetProperty("id").GetInt64());
        Assert.Equal("eb-account-1", rows[0].GetProperty("accountUid").GetString());
        Assert.Equal("2026-10-05", rows[0].GetProperty("bookingDate").GetString());
        Assert.Equal(12.50m, decimal.Parse(rows[0].GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("EUR", rows[0].GetProperty("currency").GetString());
        Assert.Equal("PINGO DOCE", rows[0].GetProperty("remittanceInformation").GetString());
        Assert.Equal(4, rows[1].GetProperty("id").GetInt64());
        Assert.Equal(77.93m, decimal.Parse(rows[1].GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Get_ShouldReturnEmptyList_WhenNoPendingRowsExist()
    {
        // Arrange
        using var factory = FactoryWithPending();
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/banktransaction");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Get_ShouldExposeNullRemittance_AsJsonNull_WhenRowHasNoRemittanceInformation()
    {
        // Arrange
        using var factory = FactoryWithPending(PendingRow(remittance: null!));
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/banktransaction");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
        var row = Assert.Single(rows);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("remittanceInformation").ValueKind);
    }

    [Fact]
    public async Task Get_ShouldReturnSuggestedCategory_WhenRowCarriesOne()
    {
        // Arrange (issue #112): the suggestion travels as {id, name} — the mirror key.
        using var factory = FactoryWithPending(PendingRow(id: 9, bookingDate: "2026-10-05") with
        {
            SuggestedCategory = new BankSuggestedCategory(3, "Restaurants"),
        });
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/banktransaction");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement[]>())!);
        var suggestion = row.GetProperty("suggestedCategory");
        Assert.Equal(3, suggestion.GetProperty("id").GetInt64());
        Assert.Equal("Restaurants", suggestion.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Get_ShouldReturnNullSuggestedCategory_WhenRowHasNoSuggestion()
    {
        // Arrange
        using var factory = FactoryWithPending(PendingRow());
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/banktransaction");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement[]>())!);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("suggestedCategory").ValueKind);
    }
}
