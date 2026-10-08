namespace GuitoApi.DataTransferObjects.Output;

/// <summary>
/// Wire shape of a pending (unmatched) bank transaction for GET /BankTransaction
/// (issue #91): what the matching screen (#83) consumes.
/// </summary>
public sealed record BankTransactionPending(
    long Id,
    string AccountUid,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    string? RemittanceInformation,
    SuggestedCategory? SuggestedCategory);
