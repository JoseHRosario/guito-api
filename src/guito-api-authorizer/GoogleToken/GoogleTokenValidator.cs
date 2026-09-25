using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GuitoApiAuthorizer.GoogleToken
{
    /// <summary>
    /// Verifies Google ID tokens (RS256) against Google's JWKS plus the local policy:
    /// issuer, audience (GOOGLE_CLIENT_ID), expiry, and the allowed-email allowlist.
    /// Independent of the agent-key path (ADR-0003); fail-closed everywhere — including
    /// a missing `alg` and any JSON/decoding problem, which deny instead of throwing.
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

        public async Task<GoogleTokenResult> ValidateAsync(string? idToken, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(idToken))
                return Fail("missing token");
            if (_audience.Length == 0)
                return Fail("GOOGLE_CLIENT_ID not configured");

            var parts = idToken.Split('.');
            if (parts.Length != 3)
                return Fail("malformed token");

            JsonDocument? headerDocument = null;
            JsonDocument? payloadDocument = null;
            try
            {
                if (!TryDecodeJwt(parts, out headerDocument, out payloadDocument, out var signature))
                    return Fail("malformed token");

                if (!RequiresRs256(headerDocument.RootElement))
                    return Fail("unsupported alg");

                var keyId = KeyId(headerDocument.RootElement);

                if (!TryReadExpirySeconds(payloadDocument.RootElement, out var exp))
                    return Fail("missing or invalid exp");

                return await ValidateSignedTokenAsync(parts, signature, keyId, exp, payloadDocument.RootElement,
                    cancellationToken);
            }
            catch (FormatException)
            {
                return Fail("malformed token");
            }
            catch (JsonException)
            {
                return Fail("malformed token");
            }
            finally
            {
                headerDocument?.Dispose();
                payloadDocument?.Dispose();
            }
        }

        private async Task<GoogleTokenResult> ValidateSignedTokenAsync(
            string[] parts, byte[] signature, string? keyId, long exp, JsonElement payload,
            CancellationToken cancellationToken)
        {
            var now = _nowOverride ?? DateTimeOffset.UtcNow;
            if (exp <= now.ToUnixTimeSeconds())
                return Fail("token expired");

            var keys = await _jwksClient.GetKeysAsync(cancellationToken);
            var key = keys.FirstOrDefault(k => k.Kid == keyId);
            if (key is null)
                return Fail("unknown kid");

            if (!VerifySignature(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature, key))
                return Fail("signature verification failed");

            return ValidateClaims(payload, exp);
        }

        private GoogleTokenResult ValidateClaims(JsonElement payload, long exp)
        {
            // Expiry before JWKS fetch happens in ValidateSignedTokenAsync; claims stay here.
            var issuer = payload.TryGetProperty("iss", out var iss) ? iss.GetString() : null;
            if (issuer is null || !AllowedIssuers.Contains(issuer))
                return Fail("untrusted issuer");

            var audience = payload.TryGetProperty("aud", out var aud) && aud.ValueKind == JsonValueKind.String
                ? aud.GetString()
                : null;
            if (audience != _audience)
                return Fail("audience mismatch");

            var email = payload.TryGetProperty("email", out var emailElement) ? emailElement.GetString() : null;
            if (email is null || !_allowedEmails.Contains(email.Trim().ToLowerInvariant()))
                return Fail("email not allowed");

            return new GoogleTokenResult(true, null,
                new GoogleTokenClaims(email, audience, issuer, exp));
        }

        private static bool TryDecodeJwt(
            string[] parts, out JsonDocument? headerDocument, out JsonDocument? payloadDocument, out byte[] signature)
        {
            headerDocument = JsonDocument.Parse(Base64UrlDecodeString(parts[0]));
            payloadDocument = JsonDocument.Parse(Base64UrlDecodeString(parts[1]));
            signature = Base64UrlDecodeBytes(parts[2]);
            return true;
        }

        // `alg` is required and must be RS256 — absence fails closed.
        private static bool RequiresRs256(JsonElement header) =>
            header.TryGetProperty("alg", out var alg) && alg.GetString() == "RS256";

        private static string? KeyId(JsonElement header) =>
            header.TryGetProperty("kid", out var kidElement) ? kidElement.GetString() : null;

        private static bool TryReadExpirySeconds(JsonElement payload, out long value)
        {
            value = 0;
            return payload.TryGetProperty("exp", out var expElement) &&
                   expElement.ValueKind == JsonValueKind.Number &&
                   expElement.TryGetInt64(out value);
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