using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Tests;

/// <summary>
/// Direct unit tests for DataApiPostgresBankAccountRepository (issue #89) over a fake
/// IPostgresDataApiClient: list mapping and uid-keyed upsert behavior.
/// </summary>
public class DataApiPostgresBankAccountRepositoryTests
{
    private static BankAccountUpsert BuildUpsert(string? iban = "PT5012345", DateTime? consentExpiresAt = null) =>
        new BankAccountUpsert
        {
            Uid = "eb-account-1",
            SessionId = "sess-1",
            Iban = iban,
            Name = "Main",
            Currency = "EUR",
            AspspCountry = "PT",
            AspspName = "CGD",
            ConsentStatus = "VALID",
            ConsentExpiresAt = consentExpiresAt,
        };

    [Fact]
    public async Task UpsertAsync_ShouldExecuteUidKeyedUpsert_WhenAccountIsNewOrRelinked()
    {
        // Arrange
        var client = new FakePostgresDataApiClient();
        var repository = new DataApiPostgresBankAccountRepository(client);

        // Act
        await repository.UpsertAsync(BuildUpsert(consentExpiresAt: DateTime.Parse("2026-12-01T12:00:00Z")));

        // Assert
        var sql = Assert.Single(client.ExecutedSql);
        Assert.Contains("INSERT INTO bank_accounts", sql);
        Assert.Contains("ON CONFLICT (uid) DO UPDATE", sql);
        var parameters = client.ExecutedParameters[0]!;
        Assert.Equal("uid", parameters[0].Name);
        Assert.Equal("eb-account-1", parameters[0].Value.StringValue);
        Assert.Equal("sess-1", parameters[1].Value.StringValue);
        Assert.Equal("PT5012345", parameters[2].Value.StringValue);
        Assert.Equal("2026-12-01T12:00:00Z", parameters[8].Value.StringValue);
    }

    [Fact]
    public async Task UpsertAsync_ShouldBindNullIbanAndExpiry_WhenAbsent()
    {
        // Arrange
        var client = new FakePostgresDataApiClient();
        var repository = new DataApiPostgresBankAccountRepository(client);

        // Act
        await repository.UpsertAsync(BuildUpsert(iban: null, consentExpiresAt: null));

        // Assert
        var parameters = client.ExecutedParameters[0]!;
        Assert.True(parameters[2].Value.IsNull);
        Assert.True(parameters[8].Value.IsNull);
    }

    [Fact]
    public async Task ListAsync_ShouldMapEveryColumn_WhenRowsReturn()
    {
        // Arrange
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult(
            ["uid", "session_id", "iban", "name", "currency", "aspsp_country", "aspsp_name", "consent_status", "consent_expires_at"],
            [[
                PostgresValue.FromString("eb-account-1"),
                PostgresValue.FromString("sess-1"),
                PostgresValue.Null(),
                PostgresValue.FromString("Main"),
                PostgresValue.FromString("EUR"),
                PostgresValue.FromString("PT"),
                PostgresValue.FromString("CGD"),
                PostgresValue.FromString("VALID"),
                PostgresValue.FromString("2026-12-01T12:00:00Z"),
            ]],
            0));
        var repository = new DataApiPostgresBankAccountRepository(client);

        // Act
        var accounts = await repository.ListAsync();

        // Assert
        Assert.Contains("SELECT uid, session_id, iban, name, currency", client.ExecutedSql.Single());
        var account = Assert.Single(accounts);
        Assert.Equal("eb-account-1", account.Uid);
        Assert.Equal("sess-1", account.SessionId);
        Assert.Null(account.Iban);
        Assert.Equal("Main", account.Name);
        Assert.Equal("EUR", account.Currency);
        Assert.Equal("PT", account.AspspCountry);
        Assert.Equal("CGD", account.AspspName);
        Assert.Equal("VALID", account.ConsentStatus);
        Assert.Equal(DateTime.Parse("2026-12-01T12:00:00Z"), account.ConsentExpiresAt);
    }
}