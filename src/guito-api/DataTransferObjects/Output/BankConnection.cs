namespace GuitoApi.DataTransferObjects.Output;

/// <summary>One linked bank account as shown on the Settings bank-connection card (issue #116).</summary>
public class BankConnection
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Masked IBAN (only the last 4 digits leave the API) — empty when the ASPSP provided none.</summary>
    public string IbanMasked { get; set; } = string.Empty;

    public string Currency { get; set; } = string.Empty;
    public string AspspName { get; set; } = string.Empty;
    public string AspspCountry { get; set; } = string.Empty;

    /// <summary>EB consent state, e.g. VALID — an expired consent drives the 409 reconnect flow.</summary>
    public string ConsentStatus { get; set; } = string.Empty;
    public DateTime? ConsentExpiresAt { get; set; }
}

/// <summary>Wrapped list of linked accounts (the repo's output-shape convention, cf. ExpenseListLatest).</summary>
public class BankConnectionList
{
    public List<BankConnection> Accounts { get; set; } = [];
}