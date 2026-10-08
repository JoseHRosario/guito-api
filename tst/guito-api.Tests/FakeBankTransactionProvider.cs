using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Tests;

/// <summary>
/// Hermetic fake of the bank transaction-fetch port (direct sync-service tests,
/// issue #112): scripts the pages each ListTransactionsAsync call returns.
/// </summary>
public class FakeBankTransactionProvider : IBankTransactionProvider
{
    public Queue<BankTransactionSourcePage> Pages { get; } = new();

    public Task<BankTransactionSourcePage> ListTransactionsAsync(
        string accountUid, DateOnly? dateFrom, DateOnly? dateTo, string? continuationKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Pages.Count > 0 ? Pages.Dequeue()
            : new BankTransactionSourcePage([], ContinuationKey: null));
}
