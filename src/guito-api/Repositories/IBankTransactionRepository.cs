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
    /// Imports a movement and returns its selected identity and whether it was newly stored.
    /// Reliable identities refresh only unlinked rows, preserving Ignored decisions.
    /// Ambiguous occurrence slots skip repeats without claiming movement identity.
    /// </summary>
    Task<BankTransactionImportResult> ImportAsync(BankTransactionInsert transaction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all stored transactions, optionally filtered by review status,
    /// ordered by booking day descending then numeric ID descending.
    /// </summary>
    Task<IReadOnlyList<BankTransactionDetail>> ListAsync(string reviewStatus = "all", CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BankLinkedExpense>> ListSuggestionsAsync(long id, CancellationToken cancellationToken = default);
    Task<BankTransactionDetail> ReadAsync(long id, CancellationToken cancellationToken = default);
    Task<BankTransactionDetail> MatchAsync(long id, int expenseId, CancellationToken cancellationToken = default);
    Task<BankTransactionDetail> UnmatchAsync(long id, int expenseId, CancellationToken cancellationToken = default);

    /// <summary>Atomically changes an unlinked row to Needs review or Ignored; rejects missing/matched rows.</summary>
    Task<BankTransactionDetail> ChangeReviewStatusAsync(long id, MatchStatus matchStatus, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the Jev-suggested category (issue #112) on the transaction with the
    /// given sync_key — the dedup anchor returned by the insert. Only called for NEW
    /// rows and only when a category was resolved; an absent suggestion stays NULL
    /// (fail-open, ADR-0014).
    /// </summary>
    Task UpdateSuggestedCategoryAsync(string syncKey, long? categoryId, CancellationToken cancellationToken = default);
}
