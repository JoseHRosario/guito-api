namespace GuitoApi.Infrastructure.Postgres;

/// <summary>
/// The fake seam over the RDS Data API (issue #87, ADR-0013). Higher layers — repositories
/// (#88), services, tests — depend on this interface and never on Amazon.RDSDataService.
/// A fake of this interface scripts result sets/exceptions hermetically.
/// </summary>
public interface IPostgresDataApiClient
{
    /// <summary>Executes one statement. <paramref name="transactionId"/> scopes it into an open transaction.</summary>
    Task<PostgresResult> ExecuteAsync(
        string sql,
        IReadOnlyList<PostgresParameter>? parameters = null,
        string? transactionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Begins a Data API transaction; returns its transaction id for subsequent statements.</summary>
    Task<string> BeginTransactionAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(string transactionId, CancellationToken cancellationToken = default);

    Task RollbackAsync(string transactionId, CancellationToken cancellationToken = default);
}