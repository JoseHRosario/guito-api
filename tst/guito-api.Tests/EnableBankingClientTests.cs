using System.Net;
using GuitoApi.Infrastructure.EnableBanking;

namespace GuitoApi.Tests;

/// <summary>
/// EnableBankingClient behavior over the faked EB transport (issue #89): request shapes,
/// payload parsing, pagination fields, and upstream error mapping.
/// </summary>
public class EnableBankingClientTests
{
    private readonly FakeEnableBankingTransport _transport = new();
    private readonly EnableBankingClient _client;

    public EnableBankingClientTests()
    {
        _client = new EnableBankingClient(_transport);
    }

    [Fact]
    public async Task StartAuthorizationAsync_ShouldPostAuthWithAspspStateAndRedirect_WhenCalled()
    {
        // Arrange
        _transport.Enqueue(200, """{"url": "https://auth.enablebanking.com/ais/start?sessionid=s1"}""");

        // Act
        var result = await _client.StartAuthorizationAsync("CGD", "PT", "state-1", "https://cb");

        // Assert
        Assert.Equal("https://auth.enablebanking.com/ais/start?sessionid=s1", result.Url);
        var request = Assert.Single(_transport.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("auth", request.Path);
        Assert.Contains("\"aspsp\":{\"name\":\"CGD\",\"country\":\"PT\"}", request.JsonBody);
        Assert.Contains("\"state\":\"state-1\"", request.JsonBody);
        Assert.Contains("\"redirect_url\":\"https://cb\"", request.JsonBody);
    }

    [Fact]
    public async Task AuthorizeSessionAsync_ShouldPostSessionsWithCodeAndParseAccounts_WhenCalled()
    {
        // Arrange
        _transport.Enqueue(200, """
        {
            "session_id": "sess-1",
            "aspsp": {"name": "CGD", "country": "PT"},
            "access": {"valid_until": "2026-12-01T12:00:00Z"},
            "accounts": [
                {"uid": "uid-1", "account_id": {"iban": "PT5012345"}, "name": "Main", "currency": "EUR"},
                {"uid": "uid-2", "name": "Savings", "currency": "EUR"}
            ]
        }
        """);

        // Act
        var session = await _client.AuthorizeSessionAsync("auth-code");

        // Assert
        var request = Assert.Single(_transport.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("sessions", request.Path);
        Assert.Equal("""{"code":"auth-code"}""", request.JsonBody);
        Assert.Equal("sess-1", session.SessionId);
        Assert.Equal("CGD", session.AspspName);
        Assert.Equal("PT", session.AspspCountry);
        Assert.Equal(2, session.Accounts.Count);
        Assert.Equal("uid-1", session.Accounts[0].Uid);
        Assert.Equal("PT5012345", session.Accounts[0].Iban);
        Assert.Equal(DateTime.Parse("2026-12-01T12:00:00Z"), session.AccessValidUntil);
        Assert.Equal("uid-2", session.Accounts[1].Uid);
        Assert.Null(session.Accounts[1].Iban);
    }

    [Fact]
    public async Task ListTransactionsAsync_ShouldGetWithDatesStrategyAndContinuation_WhenCalled()
    {
        // Arrange
        _transport.Enqueue(200, """{"transactions": [], "continuation_key": null}""");

        // Act
        await _client.ListTransactionsAsync("uid-1", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 8), "page-2");

        // Assert
        var request = Assert.Single(_transport.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("accounts/uid-1/transactions?date_from=2026-10-01&date_to=2026-10-08&continuation_key=page-2&strategy=default", request.Path);
    }

    [Fact]
    public async Task ListTransactionsAsync_ShouldOmitAbsentQueryParameters_WhenCalledWithoutDatesOrKey()
    {
        // Arrange
        _transport.Enqueue(200, """{"transactions": [], "continuation_key": null}""");

        // Act
        await _client.ListTransactionsAsync("uid-1", null, null, null);

        // Assert
        var request = Assert.Single(_transport.Requests);
        Assert.Equal("accounts/uid-1/transactions?strategy=default", request.Path);
    }

    [Fact]
    public async Task ListTransactionsAsync_ShouldParseTransactions_WhenPageReturnsRows()
    {
        // Arrange
        _transport.Enqueue(200, """
        {
            "transactions": [
                {
                    "transaction_id": "tx-1",
                    "entry_reference": "ref-1",
                    "transaction_amount": {"amount": "-77.93", "currency": "EUR"},
                    "credit_debit_indicator": "DBIT",
                    "status": "BOOK",
                    "booking_date": "2024-04-12",
                    "remittance_information": ["DD PT73", "MEO SA"],
                    "note": null
                },
                {
                    "transaction_amount": {"amount": "bogus", "currency": "EUR"},
                    "status": "BOOK",
                    "booking_date": "2024-04-12"
                }
            ],
            "continuation_key": "next-page"
        }
        """);

        // Act
        var page = await _client.ListTransactionsAsync("uid-1", null, null, null);

        // Assert
        Assert.Equal("next-page", page.ContinuationKey);
        Assert.Single(page.Transactions);
        var transaction = page.Transactions[0];
        Assert.Equal("tx-1", transaction.TransactionId);
        Assert.Equal("ref-1", transaction.EntryReference);
        Assert.Equal(new DateOnly(2024, 4, 12), transaction.BookingDate);
        Assert.Equal(-77.93m, transaction.Amount);
        Assert.Equal("EUR", transaction.Currency);
        Assert.Equal("DBIT", transaction.CreditDebitIndicator);
        Assert.Equal("BOOK", transaction.Status);
        Assert.Equal("DD PT73 MEO SA", transaction.RemittanceInformation);
        Assert.Null(transaction.Note);
    }

    [Fact]
    public async Task ListTransactionsAsync_ShouldThrowUpstreamException_WhenApiErrors()
    {
        // Arrange
        _transport.Enqueue(401, """{"status": 401, "message": "Authorization failed", "details": []}""");

        // Act
        var exception = await Assert.ThrowsAsync<EnableBankingApiException>(
            () => _client.ListTransactionsAsync("uid-1", null, null, null));

        // Assert
        Assert.Equal(401, exception.StatusCode);
        Assert.Contains("Authorization failed", exception.Message);
    }
}