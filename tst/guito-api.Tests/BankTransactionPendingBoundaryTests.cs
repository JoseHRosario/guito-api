using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.Model;

namespace GuitoApi.Tests;

/// <summary>
/// External behavior of GET /BankTransaction (issue #91): returns the pending
/// (unmatched) rows exactly as the repository orders them, empty when none —
/// the matching screen's (#83) candidate feed. Hermetic over the fake
/// IBankTransactionRepository.
/// </summary>
public class BankTransactionPendingBoundaryTests
{
    private static CustomWebApplicationFactory FactoryWithPending(params BankTransactionPendingDetail[] pending)
    {
        var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        factory.BankTransactions.Pending.AddRange(pending);
        return factory;
    }

    private static BankTransactionPendingDetail PendingRow(
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
        var rows = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Equal(2, rows.Length);
        Assert.Equal(9, rows[0].GetProperty("id").GetInt64());
        Assert.Equal("eb-account-1", rows[0].GetProperty("accountUid").GetString());
        Assert.Equal("2026-10-05", rows[0].GetProperty("bookingDate").GetString());
        Assert.Equal(12.50m, rows[0].GetProperty("amount").GetDecimal());
        Assert.Equal("EUR", rows[0].GetProperty("currency").GetString());
        Assert.Equal("PINGO DOCE", rows[0].GetProperty("remittanceInformation").GetString());
        Assert.Equal(4, rows[1].GetProperty("id").GetInt64());
        Assert.Equal(77.93m, rows[1].GetProperty("amount").GetDecimal());
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
        var rows = await response.Content.ReadFromJsonAsync<JsonElement[]>();
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
        var rows = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        var row = Assert.Single(rows);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("remittanceInformation").ValueKind);
    }
}
