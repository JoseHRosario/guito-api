using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.Model;

namespace GuitoApi.Tests;

/// <summary>
/// External behavior of POST /BankTransaction/sync (issue #90): {fetched,new} shape,
/// 7-day window + strategy=default only, BOOK/DBIT filtering, sync_key idempotency
/// (re-sync adds 0 duplicates), and the 409 reconnect path — all hermetic.
/// </summary>
public class BankTransactionSyncBoundaryTests
{
    private const string SyncPage = """
    {
        "transactions": [
            {
                "transaction_id": "tx-1",
                "entry_reference": "ref-1",
                "transaction_amount": {"amount": "-77.93", "currency": "EUR"},
                "credit_debit_indicator": "DBIT",
                "status": "BOOK",
                "booking_date": "2026-10-02",
                "remittance_information": ["COMPRA 9166 MEO"],
                "creditor": {"name": "MEO SA"}
            },
            {
                "transaction_id": "tx-2",
                "transaction_amount": {"amount": "300.00", "currency": "EUR"},
                "credit_debit_indicator": "CRDT",
                "status": "BOOK",
                "booking_date": "2026-10-02",
                "remittance_information": ["SALARIO"]
            },
            {
                "transaction_id": "tx-3",
                "transaction_amount": {"amount": "-5.00", "currency": "EUR"},
                "credit_debit_indicator": "DBIT",
                "status": "PDNG",
                "booking_date": "2026-10-02",
                "remittance_information": ["PENDENTE"]
            }
        ],
        "continuation_key": null
    }
    """;

    private static BankAccountSummary LinkedAccount(string uid = "uid-1") => new()
    {
        Uid = uid,
        SessionId = "sess-1",
        Name = "Main",
        Currency = "EUR",
        AspspCountry = "PT",
        AspspName = "CGD",
        ConsentStatus = "VALID",
    };

    private static CustomWebApplicationFactory EnabledFactory()
    {
        var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        factory.BankAccounts.Accounts.Add(LinkedAccount());
        return factory;
    }

    private static async Task<JsonElement> PostSync(HttpClient client)
    {
        var response = await client.PostAsync("/banktransaction/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Sync_ShouldReturnFetchedAndNew_WhenBookedDebitRowsArrive()
    {
        // Arrange
        using var factory = EnabledFactory();
        factory.EnableBankingTransport.Enqueue(200, SyncPage);
        var client = factory.CreateClient();

        // Act
        var body = await PostSync(client);

        // Assert — only the BOOK+DBIT row counts as fetched; CRDT and PDNG never do.
        Assert.Equal(1, body.GetProperty("fetched").GetInt32());
        Assert.Equal(1, body.GetProperty("new").GetInt32());
        var insert = Assert.Single(factory.BankTransactions.Inserts);
        Assert.Equal("uid-1", insert.AccountUid);
        Assert.Equal(77.93m, insert.Amount);
        Assert.Equal("DBIT", insert.Direction);
        Assert.Equal("BOOK", insert.Status);
        Assert.Equal("COMPRA 9166 MEO", insert.RemittanceInformation);
        Assert.False(string.IsNullOrWhiteSpace(insert.SyncKey));
    }

    [Fact]
    public async Task Sync_ShouldUseOnlyThe7DayWindowAndDefaultStrategy_WhenCalled()
    {
        // Arrange
        using var factory = EnabledFactory();
        factory.EnableBankingTransport.Enqueue(200, SyncPage);
        var client = factory.CreateClient();

        // Act
        await PostSync(client);

        // Assert
        var request = Assert.Single(factory.EnableBankingTransport.Requests);
        Assert.StartsWith("accounts/uid-1/transactions?date_from=", request.Path);
        Assert.Contains("&date_to=", request.Path);
        Assert.EndsWith("&strategy=default", request.Path);
        // Window is exactly 7 days, ending today (UTC).
        var path = request.Path;
        var from = DateOnly.Parse(path.Split("date_from=")[1].Split('&')[0]);
        var to = DateOnly.Parse(path.Split("date_to=")[1].Split('&')[0]);
        Assert.Equal(7, to.DayNumber - from.DayNumber);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), to);
    }

    [Fact]
    public async Task Sync_ShouldPaginateUntilContinuationKeyIsNull_WhenPagesReturn()
    {
        // Arrange
        using var factory = EnabledFactory();
        factory.EnableBankingTransport.Enqueue(200, SyncPage.Replace("\"continuation_key\": null", "\"continuation_key\": \"page-2\""));
        factory.EnableBankingTransport.Enqueue(200, SyncPage
            .Replace("\"transaction_id\": \"tx-1\"", "\"transaction_id\": \"tx-4\"")
            .Replace("\"entry_reference\": \"ref-1\"", "\"entry_reference\": \"ref-4\"")
            .Replace("\"transaction_amount\": {\"amount\": \"-77.93\"", "\"transaction_amount\": {\"amount\": \"-12.50\"")
            .Replace("\"creditor\": {\"name\": \"MEO SA\"}", "\"creditor\": {\"name\": \"Pingo Doce\"}"));
        var client = factory.CreateClient();

        // Act
        var body = await PostSync(client);

        // Assert
        Assert.Equal(2, factory.EnableBankingTransport.Requests.Count);
        Assert.EndsWith("continuation_key=page-2&strategy=default", factory.EnableBankingTransport.Requests[1].Path);
        Assert.Equal(2, body.GetProperty("fetched").GetInt32());
        Assert.Equal(2, body.GetProperty("new").GetInt32());
    }

    [Fact]
    public async Task Sync_ShouldAddZeroDuplicates_WhenReSyncingTheSameWindow()
    {
        // Arrange — second sync over the same window: the sync_keys now conflict.
        using var factory = EnabledFactory();
        factory.EnableBankingTransport.Enqueue(200, SyncPage);
        factory.EnableBankingTransport.Enqueue(200, SyncPage);
        var client = factory.CreateClient();

        // Act
        var first = await PostSync(client);
        var second = await PostSync(client);

        // Assert
        Assert.Equal(1, first.GetProperty("new").GetInt32());
        Assert.Equal(1, second.GetProperty("fetched").GetInt32());
        Assert.Equal(0, second.GetProperty("new").GetInt32());
        Assert.Equal(2, factory.BankTransactions.Inserts.Count);
        Assert.Single(factory.BankTransactions.Inserts.Select(i => i.SyncKey).Distinct());
    }

    [Fact]
    public async Task Sync_ShouldReturn409Reconnect_WhenNoAccountIsLinked()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        var client = factory.CreateClient();

        // Act
        var response = await client.PostAsync("/banktransaction/sync", content: null);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.EnableBankingTransport.Requests);
    }

    [Fact]
    public async Task Sync_ShouldReturn429_WhenEbRateLimits()
    {
        // Arrange
        using var factory = EnabledFactory();
        factory.EnableBankingTransport.Enqueue(429, """{"status": 429, "message": "Too many requests", "details": []}""");
        var client = factory.CreateClient();

        // Act
        var response = await client.PostAsync("/banktransaction/sync", content: null);

        // Assert
        Assert.Equal((HttpStatusCode)429, response.StatusCode);
    }

    [Fact]
    public void ComputeSyncKey_ShouldBeDeterministicPerComposite_WhenSameRowIsRecomputed()
    {
        // Arrange — ADR-0013 composite: uid | booking_date | amount | direction |
        // entry_reference | counterparty, hashed with SHA-256.
        var account = LinkedAccount();
        var transaction = new GuitoApi.Infrastructure.EnableBanking.EnableBankingTransaction(
            TransactionId: "tx-1", EntryReference: "ref-1", BookingDate: new DateOnly(2026, 10, 2),
            Amount: -77.93m, Currency: "EUR", CreditDebitIndicator: "DBIT", Status: "BOOK",
            RemittanceInformation: "COMPRA", Note: null, CounterpartyName: "MEO SA");

        // Act
        var key1 = GuitoApi.Services.BankTransactions.SyncBankTransactionsService.ComputeSyncKey(account, transaction);
        var key2 = GuitoApi.Services.BankTransactions.SyncBankTransactionsService.ComputeSyncKey(account, transaction);

        // Assert
        Assert.Equal(key1, key2);
        Assert.Equal(64, key1.Length);
        var otherCounterparty = transaction with { CounterpartyName = "Other SA" };
        Assert.NotEqual(key1, GuitoApi.Services.BankTransactions.SyncBankTransactionsService.ComputeSyncKey(account, otherCounterparty));
        var otherEntry = transaction with { EntryReference = "ref-2" };
        Assert.NotEqual(key1, GuitoApi.Services.BankTransactions.SyncBankTransactionsService.ComputeSyncKey(account, otherEntry));
    }
}