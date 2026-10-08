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

    /// <summary>Scripted pending rows returned by ListPendingAsync (issue #91 GET test).</summary>
    public List<BankTransactionPendingDetail> Pending { get; } = [];

    public Task<bool> InsertOrSkipAsync(BankTransactionInsert transaction, CancellationToken cancellationToken = default)
    {
        Inserts.Add(transaction);
        return Task.FromResult(ExistingSyncKeys.Add(transaction.SyncKey));
    }

    public Task<IReadOnlyList<BankTransactionPendingDetail>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BankTransactionPendingDetail>>([.. Pending]);

    /// <summary>Suggestion updates recorded as (sync_key, category id), in call order (issue #112).</summary>
    public List<(string SyncKey, long? CategoryId)> SuggestionUpdates { get; } = [];

    public Task UpdateSuggestedCategoryAsync(string syncKey, long? categoryId, CancellationToken cancellationToken = default)
    {
        SuggestionUpdates.Add((syncKey, categoryId));
        return Task.CompletedTask;
    }
}
