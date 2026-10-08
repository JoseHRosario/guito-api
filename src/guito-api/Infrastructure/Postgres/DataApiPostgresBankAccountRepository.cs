using System.Globalization;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Infrastructure.Postgres;

/// <summary>
/// IBankAccountRepository over the Data API (issue #89, ADR-0013): raw SQL + small
/// mappers against the bank_accounts table (002_bank_transactions.sql), executed
/// through IPostgresDataApiClient — same shape as the transactions repository (#88).
/// </summary>
public class DataApiPostgresBankAccountRepository(IPostgresDataApiClient client) : IBankAccountRepository
{
    private const string ListSql = """
        SELECT uid, session_id, iban, name, currency, aspsp_country, aspsp_name, consent_status, consent_expires_at
        FROM bank_accounts
        ORDER BY name, uid
        """;

    private const string UpsertSql = """
        INSERT INTO bank_accounts
            (uid, session_id, iban, name, currency, aspsp_country, aspsp_name, consent_status, consent_expires_at)
        VALUES
            (:uid, :session_id, :iban, :name, :currency, :aspsp_country, :aspsp_name, :consent_status, :consent_expires_at)
        ON CONFLICT (uid) DO UPDATE SET
            session_id = EXCLUDED.session_id,
            iban = EXCLUDED.iban,
            name = EXCLUDED.name,
            currency = EXCLUDED.currency,
            aspsp_country = EXCLUDED.aspsp_country,
            aspsp_name = EXCLUDED.aspsp_name,
            consent_status = EXCLUDED.consent_status,
            consent_expires_at = EXCLUDED.consent_expires_at
        """;

    public async Task<IReadOnlyList<BankAccountSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await client.ExecuteAsync(ListSql, cancellationToken: cancellationToken);
        return result.Rows.Select(ToSummary).ToList();
    }

    public async Task UpsertAsync(BankAccountUpsert account, CancellationToken cancellationToken = default)
    {
        await client.ExecuteAsync(UpsertSql, ToParameters(account), cancellationToken: cancellationToken);
    }

    // Positional access mirrors the SELECT column order above (same convention as the
    // transactions repository, #88).

    private static IReadOnlyList<PostgresParameter> ToParameters(BankAccountUpsert account) =>
    [
        new("uid", PostgresValue.FromString(account.Uid)),
        new("session_id", PostgresValue.FromString(account.SessionId)),
        new("iban", account.Iban is null ? PostgresValue.Null() : PostgresValue.FromString(account.Iban)),
        new("name", PostgresValue.FromString(account.Name)),
        new("currency", PostgresValue.FromString(account.Currency)),
        new("aspsp_country", PostgresValue.FromString(account.AspspCountry)),
        new("aspsp_name", PostgresValue.FromString(account.AspspName)),
        new("consent_status", PostgresValue.FromString(account.ConsentStatus)),
        new("consent_expires_at", account.ConsentExpiresAt is null
            ? PostgresValue.Null()
            : PostgresValue.FromString(account.ConsentExpiresAt.Value.ToString("yyyy-MM-ddTHH:mm:ssZ"))),
    ];

    private static BankAccountSummary ToSummary(IReadOnlyList<PostgresValue> row) => new()
    {
        Uid = row[0].StringValue!,
        SessionId = row[1].StringValue!,
        Iban = row[2].IsNull ? null : row[2].StringValue,
        Name = row[3].StringValue!,
        Currency = row[4].StringValue!,
        AspspCountry = row[5].StringValue!,
        AspspName = row[6].StringValue!,
        ConsentStatus = row[7].StringValue!,
        ConsentExpiresAt = row[8].IsNull
            ? null
            : DateTime.Parse(row[8].StringValue!, styles: DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
    };
}