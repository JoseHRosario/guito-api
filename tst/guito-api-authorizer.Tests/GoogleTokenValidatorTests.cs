using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GuitoApiAuthorizer.GoogleToken;
using Xunit;

namespace GuitoApiAuthorizer.Tests
{
    /// <summary>
    /// Issue #13 boundary tests for Google ID-token validation — fully hermetic:
    /// tokens are signed with locally generated RSA keys and served by a fake JWKS client;
    /// no network, no real Google, no credentials. Edge-dispatch tests live in
    /// AuthorizerFunctionTests.
    /// </summary>
    public class GoogleTokenValidatorTests
    {
        private static readonly DateTimeOffset FixedNow = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        private const string Audience = "test-client-id.apps.googleusercontent.com";
        private const string AllowedEmail = "jose@example.com";

        [Fact]
        public async Task ValidateAsync_ShouldReturnValid_WhenTokenIsSignedByGoogleAndEmailAllowed()
        {
            var result = await ValidateAsync(MakeToken());

            Assert.True(result.Valid, result.FailureReason);
            Assert.Equal(AllowedEmail, result.Claims!.Email);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenTokenIsMissing()
        {
            var result = await ValidateAsync(null);

            Assert.False(result.Valid);
            Assert.Equal("missing token", result.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenTokenIsMalformed()
        {
            var first = await ValidateAsync("not-a-jwt");
            var second = await ValidateAsync("a.b");

            Assert.False(first.Valid);
            Assert.False(second.Valid);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenTokenHeaderIsNotJson()
        {
            // Valid base64url, but not JSON — must fail closed, not throw out of the validator.
            var result = await ValidateAsync("aGVsbG8gd29ybGQ=.aGVsbG8gd29ybGQ=.aGVsbG8=");

            Assert.False(result.Valid);
            Assert.Equal("malformed token", result.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenTokenIsExpired()
        {
            var result = await ValidateAsync(MakeToken(exp: FixedNow.ToUnixTimeSeconds() - 100));

            Assert.Equal("token expired", result.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenExpiryClaimIsMissingOrNotANumber()
        {
            var missingExp = await ValidateAsync(MakeTokenWithoutClaim("exp"));
            var stringExp = await ValidateAsync(MakeTokenWithRawPayload(p =>
                p["exp"] = JsonSerializer.SerializeToElement("tomorrow")));

            Assert.Equal("missing or invalid exp", missingExp.FailureReason);
            Assert.Equal("missing or invalid exp", stringExp.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenAudienceMismatches()
        {
            var result = await ValidateAsync(MakeToken(audience: "other-client-id"));

            Assert.Equal("audience mismatch", result.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenAudienceClaimIsAnArray()
        {
            // An array-valued `aud` used to throw InvalidOperationException out of the
            // validator (Lambda 500); it must fail closed to a Deny instead.
            var result = await ValidateAsync(MakeTokenWithRawPayload(p =>
                p["aud"] = JsonSerializer.SerializeToElement(new[] { Audience, "other-client-id" }), resign: true));

            Assert.False(result.Valid);
            Assert.Equal("audience mismatch", result.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenIssuerIsNotGoogle()
        {
            var result = await ValidateAsync(MakeToken(issuer: "https://evil.example.com"));

            Assert.Equal("untrusted issuer", result.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenEmailIsNotAllowlisted()
        {
            var result = await ValidateAsync(MakeToken(email: "stranger@example.com"));

            Assert.Equal("email not allowed", result.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenTokenHasNoEmailClaim()
        {
            var result = await ValidateAsync(MakeToken(email: null));

            Assert.Equal("email not allowed", result.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenSignatureIsFromUnknownKey()
        {
            // Token signed by a key that is NOT in the served JWKS.
            var result = await ValidateAsync(MakeToken(signWithFreshKey: true));

            Assert.Equal("signature verification failed", result.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenPayloadIsTamperedWithoutResigning()
        {
            var token = MakeToken();
            var parts = token.Split('.');
            var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(
                Encoding.UTF8.GetString(Base64UrlDecode(parts[1])))!;
            payload["aud"] = "evil-client-id"; // claim altered without re-signing
            var forgedPayload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));

            var result = await ValidateAsync($"{parts[0]}.{forgedPayload}.{parts[2]}");

            Assert.False(result.Valid);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenAlgIsNotRs256()
        {
            var result = await ValidateAsync(MakeToken(alg: "HS256"));

            Assert.Equal("unsupported alg", result.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenAlgClaimIsMissing()
        {
            // `alg` is required: a header without `alg` must fail closed, not skip the check.
            var result = await ValidateAsync(MakeTokenWithoutClaim("alg", header: true));

            Assert.Equal("unsupported alg", result.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldAcceptBothIssuerForms_WhenTokenIsFromGoogle()
        {
            var bareHost = await ValidateAsync(MakeToken(issuer: "accounts.google.com"));
            var httpsHost = await ValidateAsync(MakeToken(issuer: "https://accounts.google.com"));

            Assert.True(bareHost.Valid);
            Assert.True(httpsHost.Valid);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFailClosed_WhenClientIdIsNotConfigured()
        {
            var validator = new GoogleTokenValidator(new FakeJwksClient(ServeKeys()), string.Empty, [AllowedEmail]);

            var result = await validator.ValidateAsync(MakeToken());

            Assert.Equal("GOOGLE_CLIENT_ID not configured", result.FailureReason);
        }

        [Fact]
        public async Task GetKeysAsync_ShouldServeFromCache_WhenMaxAgeHasNotExpired()
        {
            var handler = new FakeJwksHandler();
            var client = new HttpJwksClient(new HttpClient(handler));

            await client.GetKeysAsync();
            await client.GetKeysAsync();

            Assert.Equal(1, handler.RequestCount);
        }

        [Fact]
        public void ParseJwks_ShouldExtractKidNAndE_WhenJwksHasGoogleShape()
        {
            var keys = HttpJwksClient.ParseJwks(JwksJson(Jwk("kid-1")));

            var key = Assert.Single(keys);
            Assert.Equal("kid-1", key.Kid);
            Assert.Equal("modulus-value", key.N);
            Assert.Equal("AQAB", key.E);
        }

        // --- helpers ----------------------------------------------------------------------

        private static async Task<GoogleTokenResult> ValidateAsync(string? token)
        {
            var validator = new GoogleTokenValidator(
                new FakeJwksClient(ServeKeys()), Audience, [AllowedEmail], FixedNow);
            return await validator.ValidateAsync(token);
        }

        private static string ServeKeys()
        {
            var rsa = RsaKeyHolder.Key; // shared static key; never disposed (process lifetime)
            return JwksJson(JwkFromRsa("kid-1", rsa));
        }

        private static string MakeToken(
            string? audience = null,
            string issuer = "https://accounts.google.com",
            long? exp = null,
            string? email = AllowedEmail,
            string alg = "RS256",
            bool signWithFreshKey = false)
        {
            var expValue = exp ?? FixedNow.ToUnixTimeSeconds() + 600;
            var header = new Dictionary<string, string> { ["alg"] = alg, ["typ"] = "JWT", ["kid"] = "kid-1" };
            var payload = new Dictionary<string, object>
            {
                ["iss"] = issuer,
                ["aud"] = audience ?? Audience,
                ["exp"] = expValue,
                ["email"] = email!,
            };
            if (email is null)
                payload.Remove("email");

            var rsa = signWithFreshKey ? RSA.Create(2048) : RsaKeyHolder.Key;
            var signedInput = Encoding.ASCII.GetBytes(
                $"{Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload))}");
            var signature = rsa.SignData(signedInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return $"{Encoding.ASCII.GetString(signedInput)}.{Base64UrlEncode(signature)}";
        }

        private static string MakeTokenWithoutClaim(string claim, bool header = false)
        {
            var token = MakeToken();
            var parts = token.Split('.');
            var container = JsonSerializer.Deserialize<Dictionary<string, object>>(
                Encoding.UTF8.GetString(Base64UrlDecode(parts[header ? 0 : 1])))!;
            container.Remove(claim);
            var encoded = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(container));
            return header
                ? $"{encoded}.{parts[1]}.{parts[2]}"
                : $"{parts[0]}.{encoded}.{parts[2]}";
        }

        private static string MakeTokenWithRawPayload(
            Action<Dictionary<string, object>> mutate, bool resign = false)
        {
            var token = MakeToken();
            var parts = token.Split('.');
            var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(
                Encoding.UTF8.GetString(Base64UrlDecode(parts[1])))!;
            mutate(payload);
            var forgedPayload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
            if (!resign)
                return $"{parts[0]}.{forgedPayload}.{parts[2]}";

            var signedInput = Encoding.ASCII.GetBytes($"{parts[0]}.{forgedPayload}");
            var signature = RsaKeyHolder.Key.SignData(signedInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return $"{Encoding.ASCII.GetString(signedInput)}.{Base64UrlEncode(signature)}";
        }

        private static string JwkFromRsa(string kid, RSA rsa)
        {
            var parameters = rsa.ExportParameters(false);
            return JsonSerializer.Serialize(new
            {
                kty = "RSA",
                kid,
                alg = "RS256",
                n = Base64UrlEncode(parameters.Modulus!),
                e = Base64UrlEncode(parameters.Exponent!),
            });
        }

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] Base64UrlDecode(string input) => Convert.FromBase64String(
            input.Replace('-', '+').Replace('_', '/').PadRight(input.Length + (4 - input.Length % 4) % 4, '='));

        private static class RsaKeyHolder
        {
            public static readonly RSA Key = RSA.Create(2048);
        }

        private class FakeJwksClient(string json) : IJwksClient
        {
            public Task<IReadOnlyList<JsonWebKey>> GetKeysAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(HttpJwksClient.ParseJwks(json));
        }

        private class FakeJwksHandler : HttpMessageHandler
        {
            public int RequestCount;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"keys\":[]}"),
                };
                response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(60),
                };
                return Task.FromResult(response);
            }
        }

        private static string JwksJson(string keyJson) => $"{{\"keys\":[{keyJson}]}}";

        private static string Jwk(string kid) =>
            JsonSerializer.Serialize(new { kty = "RSA", kid, alg = "RS256", n = "modulus-value", e = "AQAB" });
    }
}