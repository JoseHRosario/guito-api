namespace GuitoApi.Infrastructure.Postgres;

internal sealed record PostgresExpenseHistoryHeader(
    Guid SnapshotId, DateTimeOffset ExpiresAt, long TotalCount, long Position);
