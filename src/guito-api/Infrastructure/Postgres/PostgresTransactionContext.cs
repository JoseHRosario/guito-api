namespace GuitoApi.Infrastructure.Postgres;

/// <summary>
/// Ambient transaction state for one request (issue #87, unit of work). The Unit of Work
/// sets it on Begin and clears it on Commit/Rollback; DataApiClient joins every statement
/// to it while open — the raw-SQL analogue of DbContext's ambient transaction.
/// </summary>
public class PostgresTransactionContext
{
    public string? CurrentTransactionId { get; set; }

    public bool IsOpen => CurrentTransactionId is not null;
}