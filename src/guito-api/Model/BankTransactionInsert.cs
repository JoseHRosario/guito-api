namespace GuitoApi.Model;

/// <summary>
/// One bank transaction to persist into the bank_transactions table (issue #88).
/// Amounts are stored POSITIVE (ADR-0010); only BOOK/DBIT rows reach this boundary —
/// the schema CHECKs enforce both. The account referenced by AccountUid must already
/// exist in bank_accounts (created by the consent flow, #89/#90).
/// </summary>
public class BankTransactionInsert
{
    /// <summary>The immutable Guito account key (bank_accounts PK).</summary>
    public string AccountUid { get; set; } = string.Empty;

    /// <summary>The booking date, at day precision.</summary>
    public DateOnly BookingDate { get; set; }

    /// <summary>The stored amount, strictly positive.</summary>
    public decimal Amount { get; set; }

    /// <summary>The amount currency code (e.g. EUR).</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>The EB credit/debit indicator — only DBIT rows are stored.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>The transaction's remittance information (merchant/description), when present.</summary>
    public string? RemittanceInformation { get; set; }

    /// <summary>The EB transaction status — only BOOK rows are final and stored.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Preferred reference identity or ambiguous occurrence slot; UNIQUE in the schema.
    /// Only reliable, unlinked identities can refresh current source fields.
    /// </summary>
    public string SyncKey { get; set; } = string.Empty;

    public string? EntryReference { get; set; }
    public bool IdentityReliable { get; set; }
    public string? FallbackSyncKey { get; set; }
    public DateOnly? TransactionDate { get; set; }
    public DateOnly? ValueDate { get; set; }
}
