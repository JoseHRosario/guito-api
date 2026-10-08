namespace GuitoApi.Infrastructure.EnableBanking;

/// <summary>The credentials the EB signer needs (issue #89).</summary>
/// <param name="ApplicationId">The EB application id — the JWT "kid" header value.</param>
/// <param name="PrivateKey">The application private key PEM.</param>
public record EnableBankingCredentials(string ApplicationId, string PrivateKey);