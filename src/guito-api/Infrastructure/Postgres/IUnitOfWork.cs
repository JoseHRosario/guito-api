namespace GuitoApi.Infrastructure.Postgres;

/// <summary>
/// Unit of Work over the Data API (issue #87, following the repository+UoW pattern):
/// groups several statements into one all-or-nothing unit. Repositories execute through
/// IPostgresDataApiClient without naming a transaction — statements run while a unit of
/// work is open join it automatically.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>Opens the transaction all subsequent statements (until Commit/Rollback) join.</summary>
    Task BeginAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}