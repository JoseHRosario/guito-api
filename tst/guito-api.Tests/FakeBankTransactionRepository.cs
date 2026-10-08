using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Tests;

/// <summary>
/// Hermetic fake of IBankTransactionRepository for the sync endpoint (issue #90):
/// records inserts, scripts whether each sync_key is new, keeps the stored rows.
/// </summary>
public class FakeBankTransactionRepository : IBankTransactionRepository
{
    /// <summary>sync_keys already reported as existing (conflict → InsertOrSkip returns false).</summary>
    public HashSet<string> ExistingSyncKeys { get; } = [];

    public List<BankTransactionInsert> Inserts { get; } = [];

    public Task<bool> InsertOrSkipAsync(BankTransactionInsert transaction, CancellationToken cancellationToken = default)
    {
        Inserts.Add(transaction);
        return Task.FromResult(ExistingSyncKeys.Add(transaction.SyncKey));
    }

    public Task<IReadOnlyList<BankTransactionPendingDetail>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BankTransactionPendingDetail>>([]);
}