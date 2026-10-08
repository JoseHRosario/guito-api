namespace GuitoApi.Infrastructure.EnableBanking;

/// <summary>One EB transaction row (subset of the EB Transaction schema, issue #89).</summary>
public record EnableBankingTransaction(
    string? TransactionId,
    string? EntryReference,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    string CreditDebitIndicator,
    string Status,
    string? RemittanceInformation,
    string? Note);