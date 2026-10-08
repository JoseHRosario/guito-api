namespace GuitoApi.Services.BankTransactions
{
    /// <summary>
    /// Syncs bank transactions from Enable Banking into the Postgres store
    /// (issue #90, ADR-0004/0013): default 7-day window, fetch + store, idempotent.
    /// </summary>
    public interface ISyncBankTransactionsService
    {
        Task<DataTransferObjects.Output.BankSyncResult> SyncAsync(CancellationToken cancellationToken = default);
    }
}