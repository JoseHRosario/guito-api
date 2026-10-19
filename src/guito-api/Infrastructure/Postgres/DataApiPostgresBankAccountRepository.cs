using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
        SELECT uid, session_id, iban, name, currency, aspsp_country, aspsp_name, consent_status, consent_expires_at, fetch_uid, identification_hash
        FROM bank_accounts
        ORDER BY name, uid
        """;

    private const string UpsertSql = """
        INSERT INTO bank_accounts
            (uid, session_id, iban, name, currency, aspsp_country, aspsp_name, consent_status, consent_expires_at, fetch_uid, identification_hash)
        VALUES
            (:uid, :session_id, :iban, :name, :currency, :aspsp_country, :aspsp_name, :consent_status, :consent_expires_at::timestamptz, :fetch_uid, :identification_hash)
        ON CONFLICT (uid) DO UPDATE SET
            session_id = EXCLUDED.session_id,
            iban = EXCLUDED.iban,
            name = EXCLUDED.name,
            currency = EXCLUDED.currency,
            aspsp_country = EXCLUDED.aspsp_country,
            aspsp_name = EXCLUDED.aspsp_name,
            consent_status = EXCLUDED.consent_status,
            consent_expires_at = EXCLUDED.consent_expires_at,
            fetch_uid = EXCLUDED.fetch_uid
        WHERE bank_accounts.identification_hash IS NOT DISTINCT FROM EXCLUDED.identification_hash
        """;

    public async Task<IReadOnlyList<BankAccountSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await client.ExecuteAsync(ListSql, cancellationToken: cancellationToken);
        return result.Rows.Select(ToSummary).ToList();
    }

    public async Task UpsertAsync(BankAccountUpsert account, CancellationToken cancellationToken = default)
    {
        var sql = string.IsNullOrWhiteSpace(account.IdentificationHash) ? UpsertSql
            : UpsertSql.Replace("ON CONFLICT (uid)", "ON CONFLICT (identification_hash) WHERE identification_hash IS NOT NULL", StringComparison.Ordinal);
        await client.ExecuteAsync(sql, ToParameters(account), cancellationToken: cancellationToken);
    }

    // Positional access mirrors the SELECT column order above (same convention as the
    // transactions repository, #88).

    private static IReadOnlyList<PostgresParameter> ToParameters(BankAccountUpsert account) =>
    [
        new("uid", PostgresValue.FromString(string.IsNullOrWhiteSpace(account.IdentificationHash) ? account.Uid
            : $"canonical:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.IdentificationHash)))}")),
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
        new("fetch_uid", PostgresValue.FromString(account.FetchUid ?? account.Uid)),
        new("identification_hash", string.IsNullOrWhiteSpace(account.IdentificationHash) ? PostgresValue.Null() : PostgresValue.FromString(account.IdentificationHash)),
    ];

    private static BankAccountSummary ToSummary(IReadOnlyList<PostgresValue> row) => new()
    {
        Uid = row[0].StringValue!,
        FetchUid = row[9].IsNull ? row[0].StringValue : row[9].StringValue,
        IdentificationHash = row[10].IsNull ? null : row[10].StringValue,
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