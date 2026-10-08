using GuitoApi.Model;

namespace GuitoApi.Repositories;

/// <summary>
/// Persistence for the Bank Transactions aggregate (issue #88). Implementations own
/// every storage-specific detail — today the Data API SQL for the bank_transactions
/// table — and it must never cross this boundary. Callers stay agnostic of the
/// datastore, per the ADR-0011 repository pattern.
/// </summary>
public interface IBankTransactionRepository
{
    /// <summary>
    /// Inserts one bank transaction, skipping when its SyncKey already exists
    /// (INSERT ... ON CONFLICT DO NOTHING). Returns whether the row was NEW —
    /// true drives the `{fetched, new}` response of the sync endpoint (#90).
    /// Idempotent: re-inserting a synced batch changes nothing.
    /// </summary>
    Task<bool> InsertOrSkipAsync(BankTransactionInsert transaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the pending (unmatched, expense_id IS NULL) transactions, newest
    /// bookings first — the matching screen's (#83) candidate order. Empty when
    /// none are unmatched.
    /// </summary>
    Task<IReadOnlyList<BankTransactionPendingDetail>> ListPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the Jev-suggested category (issue #112) on the transaction with the
    /// given sync_key — the dedup anchor returned by the insert. Only called for NEW
    /// rows and only when a category was resolved; an absent suggestion stays NULL
    /// (fail-open, ADR-0014).
    /// </summary>
    Task UpdateSuggestedCategoryAsync(string syncKey, long? categoryId, CancellationToken cancellationToken = default);
}
