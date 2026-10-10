namespace GuitoApi.Configuration;

/// <summary>
/// Binding for the "AppConfiguration:EnableBanking" section (issue #89, ADR-0004).
/// The application id is public (it rides in every JWT header as "kid"), so it lives
/// in config; the private key stays in SSM SecureString (ADR-0008).
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

    /// <summary>The EB application id (public; the JWT "kid" header value).</summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>Private key source: "Payload" (runtime payload, default) or "AwsSsm" (dedicated SecureString).</summary>
    public string SecretsSource { get; set; } = "Payload";

    /// <summary>The dedicated SecureString parameter name when SecretsSource is "AwsSsm".</summary>
    public string SsmParameterName { get; set; } = string.Empty;
}
