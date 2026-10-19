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

    /// <summary>Scripted pending rows returned by ListAsync (issue #91 GET test).</summary>
    public List<BankTransactionDetail> Pending { get; } = [];

    public Task<BankTransactionImportResult> ImportAsync(BankTransactionInsert transaction, CancellationToken cancellationToken = default)
    {
        Inserts.Add(transaction);
        return Task.FromResult(new BankTransactionImportResult(ExistingSyncKeys.Add(transaction.SyncKey), transaction.SyncKey));
    }

    public Task<IReadOnlyList<BankTransactionDetail>> ListAsync(string reviewStatus = "all", CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BankTransactionDetail>>([.. Pending]);

    /// <summary>Suggestion updates recorded as (sync_key, category id), in call order (issue #112).</summary>
    public List<(string SyncKey, long? CategoryId)> SuggestionUpdates { get; } = [];

    public Task<IReadOnlyList<BankLinkedExpense>> ListSuggestionsAsync(long id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Use the real repository and fake Data API for matching tests.");

    public Task<BankTransactionDetail> ReadAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Pending.Single(transaction => transaction.Id == id));

    public Task<BankTransactionDetail> MatchAsync(long id, int expenseId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Use the real repository and fake Data API for matching tests.");

    public Task<BankTransactionDetail> UnmatchAsync(long id, int expenseId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Use the real repository and fake Data API for matching tests.");

    public Task<BankTransactionDetail> ChangeReviewStatusAsync(long id, MatchStatus matchStatus, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Use the real repository and fake Data API for review tests.");

    public Task UpdateSuggestedCategoryAsync(string syncKey, long? categoryId, CancellationToken cancellationToken = default)
    {
        SuggestionUpdates.Add((syncKey, categoryId));
        return Task.CompletedTask;
    }
}
