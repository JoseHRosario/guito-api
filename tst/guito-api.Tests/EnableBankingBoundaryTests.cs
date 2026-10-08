using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.Infrastructure.EnableBanking;
using GuitoApi.Model;

namespace GuitoApi.Tests;

/// <summary>
/// External behavior of the Enable Banking adapter over the faked EB transport
/// (issue #89): /Account/transactions filtering + 409 reconnect, /BankAuth consent
/// flow endpoints, and the Dummy provider remaining untouched.
/// </summary>
public class EnableBankingBoundaryTests
{
    private static BankAccountSummary LinkedAccount(string uid = "uid-1", string sessionId = "sess-1") => new()
    {
        Uid = uid,
        SessionId = sessionId,
        Name = "Main",
        Currency = "EUR",
        AspspCountry = "PT",
        AspspName = "CGD",
        ConsentStatus = "VALID",
    };

    private const string BookedPage = """
    {
        "transactions": [
            {
                "transaction_id": "tx-1",
                "transaction_amount": {"amount": "-77.93", "currency": "EUR"},
                "credit_debit_indicator": "DBIT",
                "status": "BOOK",
                "booking_date": "2026-10-04",
                "remittance_information": ["COMPRA 9166 MEO"]
            },
            {
                "transaction_id": "tx-2",
                "transaction_amount": {"amount": "300.00", "currency": "EUR"},
                "credit_debit_indicator": "CRDT",
                "status": "BOOK",
                "booking_date": "2026-10-04",
                "remittance_information": ["SALARIO"]
            },
            {
                "transaction_id": "tx-3",
                "transaction_amount": {"amount": "-5.00", "currency": "EUR"},
                "credit_debit_indicator": "DBIT",
                "status": "PDNG",
                "booking_date": "2026-10-05",
                "remittance_information": ["PENDENTE"]
            }
        ],
        "continuation_key": null
    }
    """;

    [Fact]
    public async Task ListTransactions_ShouldKeepOnlyBookDebitRows_WithPositiveAmounts()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        factory.BankAccounts.Accounts.Add(LinkedAccount());
        factory.EnableBankingTransport.Enqueue(200, BookedPage);
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/account/transactions");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var transactions = body.GetProperty("transactions");
        Assert.Equal(1, transactions.GetArrayLength());
        Assert.Equal("tx-1", transactions[0].GetProperty("id").GetString());
        Assert.Equal(77.93m, transactions[0].GetProperty("amount").GetDecimal());
        Assert.Equal("COMPRA 9166 MEO", transactions[0].GetProperty("description").GetString());
        var request = Assert.Single(factory.EnableBankingTransport.Requests);
        Assert.Contains("strategy=default", request.Path);
    }

    [Fact]
    public async Task ListTransactions_ShouldReturn409Reconnect_WhenNoAccountIsLinked()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/account/transactions");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.EnableBankingTransport.Requests);
    }

    [Fact]
    public async Task ListTransactions_ShouldReturn409Reconnect_WhenEbReportsSessionExpired()
    {
        // Arrange — EB returns 401 for a dead session; the adapter maps it to 409.
        using var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        factory.BankAccounts.Accounts.Add(LinkedAccount());
        factory.EnableBankingTransport.Enqueue(401, """{"status": 401, "message": "Authorization failed", "details": []}""");
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/account/transactions");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ListTransactions_ShouldReturn429_WhenEbRateLimits()
    {
        // Arrange — PSD2 per-account rate limit surfaces as 429, not 409.
        using var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        factory.BankAccounts.Accounts.Add(LinkedAccount());
        factory.EnableBankingTransport.Enqueue(429, """{"status": 429, "message": "Too many requests", "details": []}""");
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/account/transactions");

        // Assert
        Assert.Equal((HttpStatusCode)429, response.StatusCode);
    }

    [Fact]
    public async Task ListTransactions_ShouldPaginateUntilContinuationKeyIsNull()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        factory.BankAccounts.Accounts.Add(LinkedAccount());
        factory.EnableBankingTransport.Enqueue(200, BookedPage.Replace("\"continuation_key\": null", "\"continuation_key\": \"page-2\""));
        factory.EnableBankingTransport.Enqueue(200, BookedPage);
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/account/transactions");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, factory.EnableBankingTransport.Requests.Count);
        Assert.EndsWith("continuation_key=page-2&strategy=default",
            factory.EnableBankingTransport.Requests[1].Path);
    }

    [Fact]
    public async Task BankAuthUrl_ShouldReturnEbRedirectUrl_WhenAspspProvided()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        factory.EnableBankingTransport.Enqueue(200, """{"url": "https://auth.enablebanking.com/ais/start?sessionid=s1"}""");
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/bankauth/url?aspsp=CGD&country=PT");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://auth.enablebanking.com/ais/start?sessionid=s1", body.GetProperty("url").GetString());
        var request = Assert.Single(factory.EnableBankingTransport.Requests);
        Assert.Equal("auth", request.Path);
        Assert.Contains("\"redirect_url\":\"https://test/BankAuth/callback\"", request.JsonBody);
    }

    [Fact]
    public async Task BankAuthCallback_ShouldFinishSessionAndUpsertAccounts_WhenCodeProvided()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        factory.EnableBankingTransport.Enqueue(200, """
        {
            "session_id": "sess-9",
            "aspsp": {"name": "CGD", "country": "PT"},
            "access": {"valid_until": "2026-12-01T12:00:00Z"},
            "accounts": [
                {"uid": "uid-1", "account_id": {"iban": "PT5012345"}, "name": "Main", "currency": "EUR"}
            ]
        }
        """);
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // Act
        var response = await client.GetAsync("/bankauth/callback?code=the-code");

        // Assert — issue #116: the callback 302s back to the SPA's Bank page; no JSON body.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://guito.web.kerumirembora.com/bank?linked=1", response.Headers.Location?.ToString());
        var upsert = Assert.Single(factory.BankAccounts.Upserts);
        Assert.Equal("uid-1", upsert.Uid);
        Assert.Equal("sess-9", upsert.SessionId);
        Assert.Equal("VALID", upsert.ConsentStatus);
        // Consent/session ids stay adapter internals — the redirect must not carry them.
        Assert.DoesNotContain("sess-9", response.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task BankAuthCallback_ShouldReturn400_WhenCodeMissing()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/bankauth/callback");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.EnableBankingTransport.Requests);
    }

    [Fact]
    public async Task AccountTransactions_ShouldServeDummyCans_WhenBankProviderIsDummy()
    {
        // Arrange — the Dummy provider must stay selectable and unchanged (ADR-0004).
        using var factory = new CustomWebApplicationFactory();
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/account/transactions");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("transactions").GetArrayLength() > 0);
        Assert.Empty(factory.EnableBankingTransport.Requests);
    }
}