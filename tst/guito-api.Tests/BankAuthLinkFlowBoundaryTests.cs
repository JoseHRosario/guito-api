using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GuitoApi.Model;

namespace GuitoApi.Tests;

/// <summary>
/// External behavior of the bank link flow's SPA-facing pieces (issue #116):
/// GET /BankAuth/callback is a PUBLIC path (EB redirects the user's browser here
/// with no credentials — the code is the credential, same precedent as
/// /Auth/token) and 302s back to the SPA with the linked-account count;
/// GET /BankConnection lists the linked accounts for the Settings card (masked
/// IBAN, consent state). All hermetic.
/// </summary>
public class BankAuthLinkFlowBoundaryTests
{
    private const string FinishSession = """
    {
        "session_id": "sess-9",
        "aspsp": { "name": "Activo Bank", "country": "PT" },
        "access": { "valid_until": "2027-01-06T13:53:32Z" },
        "accounts": [
            {
                "uid": "uid-9",
                "account_id": { "iban": "PT50000201231234567890154" },
                "owner_name": "José Rosário",
                "currency": "EUR"
            }
        ]
    }
    """;

    private static CustomWebApplicationFactory EnabledFactory()
    {
        var factory = new CustomWebApplicationFactory
        {
            UseEnableBankingProvider = true,
            UiOrigin = "https://guito.web.test",
        };
        return factory;
    }

    private static HttpClient NoRedirectClient(CustomWebApplicationFactory factory) =>
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    [Fact]
    public async Task Callback_ShouldBePublic_WhenTheBrowserArrivesWithNoCredentials()
    {
        // Arrange — no Authorization / X-Api-Key headers at all (EB redirects the user's browser)
        using var factory = EnabledFactory();
        factory.EnableBankingTransport.Enqueue(200, FinishSession);
        var client = NoRedirectClient(factory);

        // Act
        var response = await client.GetAsync("/bankauth/callback?code=eb-code-1");

        // Assert — the auth gates must NOT 401/403 this path (redirect means the pipeline ran)
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Callback_ShouldRedirectToTheSpaWithTheLinkedCount_WhenTheCodeExchanges()
    {
        // Arrange
        using var factory = EnabledFactory();
        factory.EnableBankingTransport.Enqueue(200, FinishSession);
        var client = NoRedirectClient(factory);

        // Act
        var response = await client.GetAsync("/bankauth/callback?code=eb-code-1");

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://guito.web.test/bank?linked=1", response.Headers.Location?.ToString());
        var upsert = Assert.Single(factory.BankAccounts.Upserts);
        Assert.Equal("uid-9", upsert.Uid);
        Assert.Equal("sess-9", upsert.SessionId);
        Assert.Equal("Activo Bank", upsert.AspspName);
    }

    [Fact]
    public async Task Callback_ShouldRedirectWithZero_WhenTheConsentLinkedNoAccounts()
    {
        // Arrange — EB can return a session with zero authorized accounts
        var empty = """{ "session_id": "sess-9", "aspsp": { "name": "Activo Bank", "country": "PT" }, "accounts": [] }""";
        using var factory = EnabledFactory();
        factory.EnableBankingTransport.Enqueue(200, empty);
        var client = NoRedirectClient(factory);

        // Act
        var response = await client.GetAsync("/bankauth/callback?code=eb-code-1");

        // Assert — the SPA needs a well-formed landing even for the nothing-linked case
        Assert.Equal("https://guito.web.test/bank?linked=0", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task BankConnection_ShouldListLinkedAccountsWithMaskedIban_WhenAccountsExist()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        factory.BankAccounts.Accounts.Add(new BankAccountSummary
        {
            Uid = "uid-1",
            SessionId = "sess-1",
            Name = "Conta principal",
            Iban = "PT50000201231234567890154",
            Currency = "EUR",
            AspspCountry = "PT",
            AspspName = "Activo Bank",
            ConsentStatus = "VALID",
            ConsentExpiresAt = new DateTime(2027, 1, 6, 13, 53, 32, DateTimeKind.Utc),
        });
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/bankconnection");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accounts = body.GetProperty("accounts");
        Assert.Equal(1, accounts.GetArrayLength());
        var first = accounts[0];
        Assert.Equal("Conta principal", first.GetProperty("name").GetString());
        // The IBAN is masked — the full number never leaves the API.
        Assert.DoesNotContain("PT50000201231234567890154", first.GetProperty("ibanMasked").GetString());
        Assert.EndsWith("0154", first.GetProperty("ibanMasked").GetString());
        Assert.Equal("EUR", first.GetProperty("currency").GetString());
        Assert.Equal("Activo Bank", first.GetProperty("aspspName").GetString());
        Assert.Equal("VALID", first.GetProperty("consentStatus").GetString());
    }

    [Fact]
    public async Task BankConnection_ShouldReturnAnEmptyList_WhenNoAccountIsLinked()
    {
        // Arrange
        using var factory = new CustomWebApplicationFactory { UseEnableBankingProvider = true };
        var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/bankconnection");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body.GetProperty("accounts").GetArrayLength());
    }
}