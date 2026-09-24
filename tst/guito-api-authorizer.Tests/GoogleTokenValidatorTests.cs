using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using GuitoApiAuthorizer.AgentKey;
using GuitoApiAuthorizer.GoogleToken;
using Xunit;

namespace GuitoApiAuthorizer.Tests
{
    /// <summary>
    /// Issue #13 boundary tests for the Google ID-token human auth path — fully hermetic:
    /// tokens are signed with locally generated RSA keys and served by a fake JWKS handler;
    /// no network, no real Google, no credentials.
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
            Assert.False((await ValidateAsync("not-a-jwt")).Valid);
            Assert.False((await ValidateAsync("a.b")).Valid);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenTokenIsExpired()
        {
            var result = await ValidateAsync(MakeToken(exp: FixedNow.ToUnixTimeSeconds() - 100));
            Assert.Equal("token expired", result.FailureReason);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFail_WhenAudienceMismatches()
        {
            var result = await ValidateAsync(MakeToken(audience: "other-client-id"));
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
        public async Task ValidateAsync_ShouldAcceptBothIssuerForms_WhenTokenIsFromGoogle()
        {
            Assert.True((await ValidateAsync(MakeToken(issuer: "accounts.google.com"))).Valid);
            Assert.True((await ValidateAsync(MakeToken(issuer: "https://accounts.google.com"))).Valid);
        }

        [Fact]
        public async Task ValidateAsync_ShouldFailClosed_WhenClientIdIsNotConfigured()
        {
            var validator = new GoogleTokenValidator(new FakeJwksClient(ServeKeys()), string.Empty, [AllowedEmail]);
            var result = await validator.ValidateAsync(MakeToken());
            Assert.Equal("GOOGLE_CLIENT_ID not configured", result.FailureReason);
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

        [Fact]
        public async Task HttpJwksClient_ShouldServeFromCache_WhenMaxAgeHasNotExpired()
        {
            var handler = new FakeJwksHandler();
            var client = new HttpJwksClient(new HttpClient(handler));
            await client.GetKeysAsync();
            await client.GetKeysAsync();
            Assert.Equal(1, handler.RequestCount);
        }

        // --- dispatcher (Function) tests -------------------------------------------------

        [Fact]
        public async Task FunctionHandler_ShouldReturnAllowHuman_WhenGoogleTokenIsValid()
        {
            var function = new Function(new AgentKeyValidator(new FakeKeysLoader(["agent-key"])), new StubGoogleValidator(valid: true));
            var response = await function.FunctionHandlerAsync(Request(googleToken: "some-token"), FakeContext());
            Assert.Equal("Allow", Effect(response));
            Assert.Equal("human", response.PrincipalID);
        }

        [Fact]
        public async Task FunctionHandler_ShouldReturnDeny_WhenGoogleTokenIsInvalid()
        {
            var function = new Function(new AgentKeyValidator(new FakeKeysLoader(["agent-key"])), new StubGoogleValidator(valid: false));
            var response = await function.FunctionHandlerAsync(Request(googleToken: "bad-token"), FakeContext());
            Assert.Equal("Deny", Effect(response));
        }

        [Fact]
        public async Task FunctionHandler_ShouldReturnAllowAgent_WhenApiKeyIsValid_AndNotConsultGoogleValidator()
        {
            // The Google validator must never be consulted for the agent path (independence).
            var validator = new CountingGoogleValidator(valid: true);
            var function = new Function(new AgentKeyValidator(new FakeKeysLoader(["agent-key"])), validator);
            var response = await function.FunctionHandlerAsync(Request(apiKey: "agent-key"), FakeContext());
            Assert.Equal("Allow", Effect(response));
            Assert.Equal("agent", response.PrincipalID);
            Assert.Equal(0, validator.Calls);
        }

        [Fact]
        public async Task FunctionHandler_ShouldReturnDeny_WhenNoCredentialsArePresent()
        {
            var function = new Function(new AgentKeyValidator(new FakeKeysLoader(["agent-key"])), new StubGoogleValidator(valid: true));
            var response = await function.FunctionHandlerAsync(Request(), FakeContext());
            Assert.Equal("Deny", Effect(response));
        }

        [Fact]
        public async Task FunctionHandler_ShouldAllowHumanPath_WhenKeysLoaderFails()
        {
            // Key-loader failure must not affect the human path (paths stay independent).
            var function = new Function(new AgentKeyValidator(new ThrowingKeysLoader()), new StubGoogleValidator(valid: true));
            var response = await function.FunctionHandlerAsync(Request(googleToken: "t"), FakeContext());
            Assert.Equal("Allow", Effect(response));
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
            public Task<IReadOnlyList<JsonWebKey>> GetKeysAsync() =>
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

        private class FakeKeysLoader(IReadOnlyList<string> keys) : IKeysLoader
        {
            public Task<IReadOnlyList<string>> LoadAsync() => Task.FromResult(keys);
        }

        private class ThrowingKeysLoader : IKeysLoader
        {
            public Task<IReadOnlyList<string>> LoadAsync() => throw new InvalidOperationException("boom");
        }

        private class StubGoogleValidator(bool valid) : IGoogleTokenValidator
        {
            public Task<GoogleTokenResult> ValidateAsync(string? idToken) => Task.FromResult(valid
                ? new GoogleTokenResult(true, null, new GoogleTokenClaims("jose@example.com", "aud", "iss", 1))
                : new GoogleTokenResult(false, "rejected by stub", null));
        }

        private class CountingGoogleValidator : IGoogleTokenValidator
        {
            public int Calls;
            private readonly StubGoogleValidator _inner;
            public CountingGoogleValidator(bool valid) => _inner = new StubGoogleValidator(valid);
            public Task<GoogleTokenResult> ValidateAsync(string? idToken)
            {
                Calls++;
                return _inner.ValidateAsync(idToken);
            }
        }

        private static APIGatewayCustomAuthorizerV2Request Request(
            string? apiKey = null, string? googleToken = null)
        {
            var request = new APIGatewayCustomAuthorizerV2Request();
            if (apiKey is not null)
                request.Headers ??= new Dictionary<string, string>();
            if (apiKey is not null)
                request.Headers["X-Api-Key"] = apiKey;
            if (googleToken is not null)
                (request.Headers ??= new Dictionary<string, string>())["x-google-idtoken"] = googleToken;
            return request;
        }

        private static ILambdaContext FakeContext() => new LambdaContext();

        private static string JwksJson(string keyJson) => $"{{\"keys\":[{keyJson}]}}";

        private static string Jwk(string kid) =>
            JsonSerializer.Serialize(new { kty = "RSA", kid, alg = "RS256", n = "modulus-value", e = "AQAB" });

        private sealed class NullLogger : ILambdaLogger
        {
            public void Log(string message) { }
            public void LogLine(string message) { }
        }

        private sealed class LambdaContext : ILambdaContext
        {
            public string AwsRequestId { get; } = "test";
            public IClientContext ClientContext { get; } = null!;
            public string FunctionName { get; } = "test-authorizer";
            public string FunctionVersion { get; } = "1";
            public ILambdaLogger Logger { get; } = new NullLogger();
            public string LogGroupName { get; } = "/test";
            public string LogStreamName { get; } = "stream";
            public ICognitoIdentity Identity { get; } = null!;
            public string InvokedFunctionArn { get; } = "arn:test";
            public int MemoryLimitInMB { get; } = 128;
            public TimeSpan RemainingTime => TimeSpan.FromSeconds(30);
        }

        private static string Effect(APIGatewayCustomAuthorizerV2IamResponse response) =>
            response.PolicyDocument.Statement[0].Effect;
    }
}