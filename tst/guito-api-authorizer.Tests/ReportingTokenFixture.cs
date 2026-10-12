using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GuitoApiAuthorizer.Tests;

internal sealed class ReportingTokenFixture : HttpMessageHandler
{
    private readonly RSA _key = RSA.Create(2048);
    public const string Audience = "reporting-client";
    public const string Subject = "installer-subject";
    public int Requests { get; private set; }

    public string Token(Action<Dictionary<string, object>>? change = null, bool wrongSignature = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new Dictionary<string, object>
        {
            ["iss"] = "https://accounts.google.com", ["aud"] = Audience, ["sub"] = Subject,
            ["email"] = "installer@example.com", ["email_verified"] = true, ["iat"] = now, ["exp"] = now + 600,
        };
        change?.Invoke(payload);
        var input = $"{Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "fixture" }))}.{Encode(JsonSerializer.SerializeToUtf8Bytes(payload))}";
        using var wrong = RSA.Create(2048);
        return $"{input}.{Encode((wrongSignature ? wrong : _key).SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var key = _key.ExportParameters(false);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "fixture", alg = "RS256", n = Encode(key.Modulus!), e = Encode(key.Exponent!) } } })),
        };
        response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        return Task.FromResult(response);
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    protected override void Dispose(bool disposing) { if (disposing) _key.Dispose(); base.Dispose(disposing); }
}
