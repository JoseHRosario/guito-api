namespace GuitoApi.Configuration;

/// <summary>
/// Enable Banking credentials inside the per-environment secrets payload (issue #89,
/// ADR-0004/ADR-0008): the sandbox application id (also the JWT "kid") and the
/// application private key PEM.
/// </summary>
public class EnableBankingSecrets
{
    public string ApplicationId { get; set; } = string.Empty;

    public string PrivateKey { get; set; } = string.Empty;
}