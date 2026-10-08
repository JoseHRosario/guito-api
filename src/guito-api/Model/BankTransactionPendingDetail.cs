namespace GuitoApi.Model;

/// <summary>
/// A pending (unmatched) bank transaction as the matching screen (#83) consumes it:
/// newest bookings first. Id is the real storage key (identity column), unlike the
/// Expenses aggregate where the Id is a sheet row index.
/// </summary>
public sealed record BankTransactionPendingDetail(
    long Id,
    string AccountUid,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    string? RemittanceInformation);
