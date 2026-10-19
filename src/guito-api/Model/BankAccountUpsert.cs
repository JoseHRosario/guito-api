namespace GuitoApi.Model;

/// <summary>One bank account to upsert into bank_accounts (issue #89, consent flow).</summary>
public class BankAccountUpsert
{
    /// <summary>The provider account UID received during consent; not a durable identity.</summary>
    public string Uid { get; set; } = string.Empty;

    public string? IdentificationHash { get; set; }

    public string? FetchUid { get; set; }

    /// <summary>The EB session id this account's consent belongs to.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>IBAN when the ASPSP provides one.</summary>
    public string? Iban { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;

    public string AspspCountry { get; set; } = string.Empty;

    public string AspspName { get; set; } = string.Empty;

    /// <summary>EB consent state at authorization time, e.g. VALID.</summary>
    public string ConsentStatus { get; set; } = string.Empty;

    public DateTime? ConsentExpiresAt { get; set; }
}