namespace GuitoApi.Model;

/// <summary>One linked bank account as stored in bank_accounts (issue #89).</summary>
public class BankAccountSummary
{
    /// <summary>The Enable Banking account uid (bank_accounts PK).</summary>
    public string Uid { get; set; } = string.Empty;

    /// <summary>The EB session id this account's consent belongs to.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>IBAN when the ASPSP provides one.</summary>
    public string? Iban { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public string AspspCountry { get; set; } = string.Empty;

    public string AspspName { get; set; } = string.Empty;

    /// <summary>EB consent state, e.g. VALID — expired consent drives the 409 reconnect flow.</summary>
    public string ConsentStatus { get; set; } = string.Empty;

    public DateTime? ConsentExpiresAt { get; set; }
}