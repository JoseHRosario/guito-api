namespace GuitoApi.Infrastructure.EnableBanking;

/// <summary>One page of EB transactions plus the continuation key (null when last page).</summary>
public record EnableBankingTransactionsPage(
    IReadOnlyList<EnableBankingTransaction> Transactions,
    string? ContinuationKey);