using GuitoApi.Model;

namespace GuitoApi.Repositories;

/// <summary>
/// Transaction-fetch port of the bank provider (issues #89/#90, ADR-0004/0011): the
/// application layer's only view of bank transaction fetching. Implemented by the
/// Enable Banking adapter in Infrastructure; a future second provider would implement
/// the same port. Throws ProblemException (409 reconnect / 429 rate limit / 502).
/// </summary>
public interface IBankTransactionProvider
{
    /// <summary>One page of transactions; ContinuationKey is null on the last page.</summary>
    Task<BankTransactionSourcePage> ListTransactionsAsync(
        string accountUid, DateOnly? dateFrom, DateOnly? dateTo, string? continuationKey,
        CancellationToken cancellationToken = default);
}