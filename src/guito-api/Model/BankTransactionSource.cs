namespace GuitoApi.Model;

/// <summary>
/// One transaction row as returned by the bank provider (issues #89/#90, ADR-0004):
/// provider-shaped but provider-agnostic — the Infrastructure implementation maps the
/// Enable Banking wire JSON onto this. Amount keeps the provider's sign; ADR-0010
/// normalization to positive happens at the storage boundary.
/// </summary>
public record BankTransactionSource(
    string? TransactionId,
    string? EntryReference,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    string CreditDebitIndicator,
    string Status,
    string? RemittanceInformation,
    string? Note,
    string? CounterpartyName);