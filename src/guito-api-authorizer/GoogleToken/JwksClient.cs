using System.Text.Json;

namespace GuitoApiAuthorizer.GoogleToken
{
    /// <summary>Serves Google's public signing keys (JWKS) for ID-token verification.</summary>
    public interface IJwksClient
    {
        Task<IReadOnlyList<JsonWebKey>> GetKeysAsync();
    }

    public record JsonWebKey(string Kid, string N, string E);

    /// <summary>
    /// Fetches JWKS from Google's certs endpoint and caches per the response's Cache-Control
    /// max-age (falling back to 1h). Test seam: an HttpMessageHandler can be injected.
    /// </summary>
    public class HttpJwksClient : IJwksClient
    {
        public const string GoogleCertsUrl = "https://www.googleapis.com/oauth2/v3/certs";
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(60);

        private readonly HttpClient _httpClient;
        private IReadOnlyList<JsonWebKey>? _cached;
        private DateTimeOffset _cachedUntil;

        public HttpJwksClient(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
        }

        public async Task<IReadOnlyList<JsonWebKey>> GetKeysAsync()
        {
            if (_cached is not null && DateTimeOffset.UtcNow < _cachedUntil)
                return _cached;

            var response = await _httpClient.GetAsync(GoogleCertsUrl);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var keys = ParseJwks(json);

            _cached = keys;
            _cachedUntil = DateTimeOffset.UtcNow + GetCacheTtl(response.Headers.CacheControl?.MaxAge);
            return keys;
        }

        public static IReadOnlyList<JsonWebKey> ParseJwks(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("keys").EnumerateArray()
                .Select(k => new JsonWebKey(
                    k.GetProperty("kid").GetString() ?? throw new InvalidOperationException("JWK missing kid"),
                    k.GetProperty("n").GetString() ?? throw new InvalidOperationException("JWK missing n"),
                    k.GetProperty("e").GetString() ?? throw new InvalidOperationException("JWK missing e")))
                .ToList();
        }

        private static TimeSpan GetCacheTtl(TimeSpan? maxAge) =>
            maxAge is { } age && age > TimeSpan.Zero ? age : DefaultTtl;
    }
}