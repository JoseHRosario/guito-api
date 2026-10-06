using Microsoft.Extensions.Logging;

namespace GuitoApi.Infrastructure.Postgres;

/// <summary>
/// Data API implementation of IUnitOfWork: one transaction per unit, ambient via
/// PostgresTransactionContext. Disposing an open unit rolls it back — a statement
/// sequence that never commits changes nothing (the all-or-nothing guarantee).
/// </summary>
public class DataApiUnitOfWork : IUnitOfWork
{
    private readonly IPostgresDataApiClient _client;
    private readonly PostgresTransactionContext _transactionContext;
    private readonly ILogger<DataApiUnitOfWork>? _logger;

    public DataApiUnitOfWork(
        IPostgresDataApiClient client,
        PostgresTransactionContext transactionContext,
        ILogger<DataApiUnitOfWork>? logger = null)
    {
        _client = client;
        _transactionContext = transactionContext;
        _logger = logger;
    }

    public async Task BeginAsync(CancellationToken cancellationToken = default)
    {
        if (_transactionContext.IsOpen)
            throw new InvalidOperationException("A unit of work is already open in this scope.");

        _transactionContext.CurrentTransactionId = await _client.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        var transactionId = RequireOpenTransaction();
        _transactionContext.CurrentTransactionId = null;
        await _client.CommitAsync(transactionId, cancellationToken);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        var transactionId = RequireOpenTransaction();
        _transactionContext.CurrentTransactionId = null;
        await _client.RollbackAsync(transactionId, cancellationToken);
    }

    /// <summary>Rolls the unit back if abandoned open — un-committed work leaves no trace.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_transactionContext.IsOpen)
        {
            var transactionId = _transactionContext.CurrentTransactionId!;
            _transactionContext.CurrentTransactionId = null;
            try
            {
                await _client.RollbackAsync(transactionId);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Rollback-on-dispose failed for transaction {TransactionId}", transactionId);
            }
        }

        GC.SuppressFinalize(this);
    }

    private string RequireOpenTransaction() =>
        _transactionContext.CurrentTransactionId
        ?? throw new InvalidOperationException("No unit of work is open — call BeginAsync first.");
}