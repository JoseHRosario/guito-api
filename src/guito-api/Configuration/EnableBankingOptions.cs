namespace GuitoApi.Configuration;

/// <summary>
/// Binding for the "AppConfiguration:EnableBanking" section (issue #89, ADR-0004).
/// Credentials (application id + private key) live in the secrets payload, not here.
/// </summary>
public class EnableBankingOptions
{
    /// <summary>
    /// JWT signature algorithm for the client assertion. ADR-0004 says PS256; the live EB
    /// API reference (2026-10-08) says only RS256 — sandbox verification decides (#91/#92).
    /// </summary>
    public string JwtAlgorithm { get; set; } = "PS256";

    /// <summary>
    /// Absolute callback URL registered in the EB Control Panel (e.g.
    /// https://guito-staging.api.kerumirembora.com/BankAuth/callback) — sent as
    /// redirect_url on POST /auth.
    /// </summary>
    public string AuthCallbackUrl { get; set; } = string.Empty;
}