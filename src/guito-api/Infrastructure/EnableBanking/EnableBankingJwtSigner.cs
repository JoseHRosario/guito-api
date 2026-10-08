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
    /// PS256/RS256 JWT client-assertion signer for the Enable Banking API (issue #89,
    /// ADR-0004). Header {typ=JWT, alg, kid=&lt;application id&gt;}; body {iss=enablebanking.com,
    /// aud=api.enablebanking.com, iat, exp}. The private key comes from the per-environment
    /// secrets payload (ADR-0008); the algorithm is configuration because the live EB API
    /// reference (2026-10-08) states "only RS256 is supported" while ADR-0004/0013 record
    /// PS256 "per EB docs" — see the issue #89 PR for the resolution path.
    /// </summary>
    public class EnableBankingJwtSigner : IEnableBankingJwtSigner
    {
        private const string Issuer = "enablebanking.com";
        private const string Audience = "api.enablebanking.com";
        private static readonly TimeSpan MaxTtl = TimeSpan.FromSeconds(86400);
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);

        private readonly IOptions<EnableBankingOptions> _options;
        private readonly ISecretsProvider _secretsProvider;

        public EnableBankingJwtSigner(IOptions<EnableBankingOptions> options, ISecretsProvider secretsProvider)
        {
            _options = options;
            _secretsProvider = secretsProvider;
        }

        public async Task<string> CreateTokenAsync(CancellationToken cancellationToken = default)
        {
            var secrets = (await _secretsProvider.GetAsync(cancellationToken)).EnableBanking;
            if (string.IsNullOrWhiteSpace(secrets.ApplicationId) || string.IsNullOrWhiteSpace(secrets.PrivateKey))
                throw new InvalidOperationException(
                    "Enable Banking application id / private key missing from the secrets payload.");

            using var rsa = RSA.Create();
            rsa.ImportFromPem(secrets.PrivateKey);

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

            var ttl = DefaultTtl < MaxTtl ? DefaultTtl : MaxTtl;
            var now = DateTime.UtcNow;
            var jwt = new JwtSecurityToken(
                issuer: Issuer,
                audience: Audience,
                claims: [new Claim(JwtRegisteredClaimNames.Iat,EpochTime.GetIntDate(now).ToString(), ClaimValueTypes.Integer64)],
                expires: now + ttl,
                signingCredentials: credentials);
            jwt.Header.Add("kid", secrets.ApplicationId);

            return new JwtSecurityTokenHandler().WriteToken(jwt);
        }
    }
}