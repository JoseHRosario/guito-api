using System.Globalization;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Infrastructure.Postgres;

public sealed class DataApiUserSessionRepository(IPostgresDataApiClient database) : IUserSessionRepository
{
    public async Task CreateAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        await database.ExecuteAsync("""
            INSERT INTO user_sessions (handle_hash, subject, email, encrypted_credential, expires_at)
            VALUES (:hash, :subject, :email, :credential, CAST(:expires AS timestamptz))
            """, [Text("hash", session.HandleHash), Text("subject", session.Subject), Text("email", session.Email),
                Text("credential", session.EncryptedCredential), Date("expires", session.ExpiresAt)], cancellationToken: cancellationToken);
    }

    public async Task<UserSession?> AcquireAsync(string handleHash, string lease, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var result = await database.ExecuteAsync("""
            UPDATE user_sessions SET refresh_lease = :lease, lease_expires_at = CAST(:lease_expires AS timestamptz)
            WHERE handle_hash = :hash AND expires_at > CAST(:now AS timestamptz)
                AND (refresh_lease IS NULL OR lease_expires_at <= CAST(:now AS timestamptz))
            RETURNING subject, email, encrypted_credential, expires_at::text
            """, [Text("hash", handleHash), Text("lease", lease), Date("now", now), Date("lease_expires", now.AddMinutes(2))], cancellationToken: cancellationToken);
        if (result.Rows.Count > 0) return Read(handleHash, result.Rows[0]);
        var current = await database.ExecuteAsync("""
            SELECT handle_hash FROM user_sessions WHERE handle_hash = :hash AND expires_at > CAST(:now AS timestamptz)
            """, [Text("hash", handleHash), Date("now", now)], cancellationToken: cancellationToken);
        if (current.Rows.Count > 0) throw UserSessionException.Unavailable();
        await database.ExecuteAsync("DELETE FROM user_sessions WHERE handle_hash = :hash AND expires_at <= CAST(:now AS timestamptz)",
            [Text("hash", handleHash), Date("now", now)], cancellationToken: cancellationToken);
        return null;
    }

    public async Task<bool> CompleteAsync(string handleHash, string lease, string encryptedCredential, DateTimeOffset now,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var result = await database.ExecuteAsync("""
            UPDATE user_sessions SET encrypted_credential = :credential, expires_at = CAST(:expires AS timestamptz),
                refresh_lease = NULL, lease_expires_at = NULL
            WHERE handle_hash = :hash AND refresh_lease = :lease AND expires_at > CAST(:now AS timestamptz)
                AND lease_expires_at > CAST(:now AS timestamptz)
            """, [Text("hash", handleHash), Text("lease", lease), Text("credential", encryptedCredential), Date("now", now), Date("expires", expiresAt)], cancellationToken: cancellationToken);
        return result.NumberOfRecordsUpdated == 1;
    }

    public async Task ReleaseAsync(string handleHash, string lease, CancellationToken cancellationToken = default) =>
        await database.ExecuteAsync("""
            UPDATE user_sessions SET refresh_lease = NULL, lease_expires_at = NULL WHERE handle_hash = :hash AND refresh_lease = :lease
            """, [Text("hash", handleHash), Text("lease", lease)], cancellationToken: cancellationToken);

    public async Task<bool> InvalidateAsync(string handleHash, string lease, CancellationToken cancellationToken = default)
    {
        var result = await database.ExecuteAsync("DELETE FROM user_sessions WHERE handle_hash = :hash AND refresh_lease = :lease",
            [Text("hash", handleHash), Text("lease", lease)], cancellationToken: cancellationToken);
        return result.NumberOfRecordsUpdated == 1;
    }

    public async Task DeleteAsync(string handleHash, CancellationToken cancellationToken = default) =>
        await database.ExecuteAsync("DELETE FROM user_sessions WHERE handle_hash = :hash", [Text("hash", handleHash)], cancellationToken: cancellationToken);

    private static UserSession Read(string hash, IReadOnlyList<PostgresValue> row) =>
        new(hash, row[0].StringValue!, row[1].StringValue!, row[2].StringValue!, DateTimeOffset.Parse(row[3].StringValue!, CultureInfo.InvariantCulture));
    private static PostgresParameter Text(string name, string value) => new(name, PostgresValue.FromString(value));
    private static PostgresParameter Date(string name, DateTimeOffset value) => Text(name, value.ToString("O", CultureInfo.InvariantCulture));
}
