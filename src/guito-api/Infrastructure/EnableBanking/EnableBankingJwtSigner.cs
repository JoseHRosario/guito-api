using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using GuitoApi.Configuration;
using GuitoApi.Infrastructure.Secrets;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>
    /// RS256/PS256 JWT client-assertion signer for the Enable Banking API (issue #89,
    /// ADR-0004). Header {typ=JWT, alg, kid=&lt;application id&gt;}; body {iss=enablebanking.com,
    /// aud=api.enablebanking.com, iat, exp}. The private key comes from the per-environment
    /// secrets payload (ADR-0008). Default algorithm RS256 — empirically verified against
    /// the EB sandbox (2026-10-08): PS256 is rejected with 401 "Wrong signature"; ADR-0004's
    /// PS256 claim needs an amendment. The key also fixes the drift: the staging secret
    /// guito-api/eb-staging-pk stores the PEM flattened to one line — ImportFromPem needs
    /// proper BEGIN/END newlines (proposed fix in the PR).
    /// </summary>
    public class EnableBankingJwtSigner : IEnableBankingJwtSigner
    {
        private const string Issuer = "enablebanking.com";
        private const string Audience = "api.enablebanking.com";
        // One hour — well under EB's 24h token cap; EB rejects longer TTLs.
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);

        private readonly IOptions<EnableBankingOptions> _options;
        private readonly IEnableBankingCredentialsProvider _credentialsProvider;

        public EnableBankingJwtSigner(IOptions<EnableBankingOptions> options,
            IEnableBankingCredentialsProvider credentialsProvider)
        {
            _options = options;
            _credentialsProvider = credentialsProvider;
        }

        public async Task<string> CreateTokenAsync(CancellationToken cancellationToken = default)
        {
            var ebCredentials = await _credentialsProvider.GetAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(ebCredentials.ApplicationId) || string.IsNullOrWhiteSpace(ebCredentials.PrivateKey))
                throw new InvalidOperationException(
                    "Enable Banking application id / private key missing — check the credentials provider config.");

            using var rsa = RSA.Create();
            rsa.ImportFromPem(ebCredentials.PrivateKey);

            var algorithm = _options.Value.JwtAlgorithm switch
            {
                "PS256" => SecurityAlgorithms.RsaSsaPssSha256,
                "RS256" => SecurityAlgorithms.RsaSha256,
                _ => throw new InvalidOperationException(
                    $"Unknown EnableBanking:JwtAlgorithm '{_options.Value.JwtAlgorithm}'. Supported: PS256, RS256."),
            };

            // CacheSignatureProviders=false: same workaround as the official EB C# sample —
            // signature providers must not be cached for PEM-imported keys created per call.
            var credentials = new SigningCredentials(new RsaSecurityKey(rsa), algorithm)
            {
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
            };

            var ttl = DefaultTtl;
            var now = DateTime.UtcNow;
            var jwt = new JwtSecurityToken(
                issuer: Issuer,
                audience: Audience,
                claims: [new Claim(JwtRegisteredClaimNames.Iat,EpochTime.GetIntDate(now).ToString(), ClaimValueTypes.Integer64)],
                expires: now + ttl,
                signingCredentials: credentials);
            jwt.Header.Add("kid", ebCredentials.ApplicationId);

            return new JwtSecurityTokenHandler().WriteToken(jwt);
        }
    }
}