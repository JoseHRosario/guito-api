using GuitoApi.Model;

namespace GuitoApi.Repositories;

/// <summary>
/// Persistence for the linked bank accounts of the Bank Transactions aggregate
/// (issue #89). The Enable Banking adapter stores its consent/session state here
/// (bank_accounts.session_id) and reads which accounts it may fetch. Implementations
/// own every storage-specific detail (ADR-0011).
/// </summary>
public interface IBankAccountRepository
{
    /// <summary>Lists all linked accounts (any consent status — the adapter filters).</summary>
    Task<IReadOnlyList<BankAccountSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts or refreshes one linked account keyed by the EB account uid
    /// (ON CONFLICT (uid) DO UPDATE): a re-consent updates session/consent state
    /// in place instead of duplicating the account.
    /// </summary>
    Task UpsertAsync(BankAccountUpsert account, CancellationToken cancellationToken = default);
}