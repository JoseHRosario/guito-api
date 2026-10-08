namespace GuitoApi.Model;

/// <summary>One page of provider transactions plus the continuation key (null = last page).</summary>
public record BankTransactionSourcePage(
    IReadOnlyList<BankTransactionSource> Transactions,
    string? ContinuationKey);