using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GuitoApiAuthorizer.GoogleToken
{
    public record GoogleTokenClaims(string Email, string Audience, string Issuer, long ExpiresAtSeconds);

    public record GoogleTokenResult(bool Valid, string? FailureReason, GoogleTokenClaims? Claims);

    /// <summary>
    /// Result of verifying a Google ID token's signature with a JWK. Exposed so the
    /// validator can distinguish "signature bad" from "claims bad" in tests.
    /// </summary>
    public interface IGoogleTokenValidator
    {
        Task<GoogleTokenResult> ValidateAsync(string? idToken);
    }

    /// <summary>
    /// Verifies Google ID tokens (RS256) against Google's JWKS plus the local policy:
    /// issuer, audience (GOOGLE_CLIENT_ID), expiry, and the allowed-email allowlist.
    /// Independent of the agent-key path (ADR-0003); fail-closed everywhere.
    /// </summary>
    public class GoogleTokenValidator : IGoogleTokenValidator
    {
        private static readonly string[] AllowedIssuers =
        [
            "accounts.google.com",
            "https://accounts.google.com",
        ];

        private readonly IJwksClient _jwksClient;
        private readonly string _audience;
        private readonly IReadOnlySet<string> _allowedEmails;
        private readonly DateTimeOffset? _nowOverride; // test seam

        public GoogleTokenValidator(IJwksClient jwksClient, string audience, IEnumerable<string> allowedEmails,
            DateTimeOffset? nowOverride = null)
        {
            _jwksClient = jwksClient;
            _audience = audience;
            _allowedEmails = new HashSet<string>(
                allowedEmails.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0),
                StringComparer.Ordinal);
            _nowOverride = nowOverride;
        }

        public async Task<GoogleTokenResult> ValidateAsync(string? idToken)
        {
            if (string.IsNullOrWhiteSpace(idToken))
                return Fail("missing token");
            if (_audience.Length == 0)
                return Fail("GOOGLE_CLIENT_ID not configured");

            var parts = idToken.Split('.');
            if (parts.Length != 3)
                return Fail("malformed token");

            string headerJson, payloadJson;
            byte[] signature;
            try
            {
                headerJson = Base64UrlDecodeString(parts[0]);
                payloadJson = Base64UrlDecodeString(parts[1]);
                signature = Base64UrlDecodeBytes(parts[2]);
            }
            catch (FormatException)
            {
                return Fail("malformed token");
            }

            var header = JsonDocument.Parse(headerJson).RootElement;
            if (header.TryGetProperty("alg", out var alg) && alg.GetString() != "RS256")
                return Fail("unsupported alg");
            var kid = header.TryGetProperty("kid", out var kidElement) ? kidElement.GetString() : null;

            var payload = JsonDocument.Parse(payloadJson).RootElement;

            // Expiry before JWKS fetch: expired tokens need no network round trip.
            if (!payload.TryGetProperty("exp", out var expElement) || expAdmits(expElement, out var exp) is false)
                return Fail("missing or invalid exp");
            var now = _nowOverride ?? DateTimeOffset.UtcNow;
            if (exp <= now.ToUnixTimeSeconds())
                return Fail("token expired");

            var keys = await _jwksClient.GetKeysAsync();
            var key = keys.FirstOrDefault(k => k.Kid == kid);
            if (key is null)
                return Fail("unknown kid");

            if (!VerifySignature(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature, key))
                return Fail("signature verification failed");

            var issuer = payload.TryGetProperty("iss", out var iss) ? iss.GetString() : null;
            if (issuer is null || !AllowedIssuers.Contains(issuer))
                return Fail("untrusted issuer");

            var audience = payload.TryGetProperty("aud", out var aud) ? aud.GetString() : null;
            if (audience != _audience)
                return Fail("audience mismatch");

            var email = payload.TryGetProperty("email", out var emailElement) ? emailElement.GetString() : null;
            if (email is null || !_allowedEmails.Contains(email.Trim().ToLowerInvariant()))
                return Fail("email not allowed");

            return new GoogleTokenResult(true, null,
                new GoogleTokenClaims(email, audience, issuer, exp));
        }

        private static bool expAdmits(JsonElement exp, out long value)
        {
            value = 0;
            return exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out value);
        }

        private static bool VerifySignature(byte[] signedInput, byte[] signature, JsonWebKey key)
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Base64UrlDecodeBytes(key.N),
                    Exponent = Base64UrlDecodeBytes(key.E),
                });
                return rsa.VerifyData(signedInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static GoogleTokenResult Fail(string reason) => new(false, reason, null);

        internal static string Base64UrlDecodeString(string input) =>
            Encoding.UTF8.GetString(Base64UrlDecodeBytes(input));

        internal static byte[] Base64UrlDecodeBytes(string input) =>
            Convert.FromBase64String(input.Replace('-', '+').Replace('_', '/').PadRight(
                input.Length + (4 - input.Length % 4) % 4, '='));
    }
}