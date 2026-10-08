namespace GuitoApi.Configuration;

/// <summary>
/// Binding for the "AppConfiguration:EnableBanking" section (issue #89, ADR-0004).
/// Credentials (application id + private key) live in the secrets payload, not here.
/// </summary>
public class EnableBankingOptions
{
    /// <summary>
    /// JWT signature algorithm for the client assertion. Empirically verified against
    /// the EB sandbox (2026-10-08, GET /application): PS256 → 401 "Wrong signature",
    /// RS256 → 200. ADR-0004/0013's PS256 claim was wrong — needs an ADR amendment.
    /// PS256 stays selectable for if EB ever adds PSS support.
    /// </summary>
    public string JwtAlgorithm { get; set; } = "RS256";

    /// <summary>
    /// Absolute callback URL registered in the EB Control Panel (e.g.
    /// https://guito-staging.api.kerumirembora.com/BankAuth/callback) — sent as
    /// redirect_url on POST /auth.
    /// </summary>
    public string AuthCallbackUrl { get; set; } = string.Empty;

    /// <summary>EB API base URL (sandbox and production share it; kept configurable for tests).</summary>
    public string ApiBaseUrl { get; set; } = "https://api.enablebanking.com/";
}