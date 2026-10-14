namespace GuitoApi.Model;

/// <summary>
/// A pending (unmatched) bank transaction as the matching screen (#83) consumes it:
/// newest bookings first. Id is the immutable database-generated Int64 identity.
/// </summary>
public sealed record BankTransactionPendingDetail(
    long Id,
    string AccountUid,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    string? RemittanceInformation,
    BankSuggestedCategory? SuggestedCategory = null);
