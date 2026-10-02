using System.Text.Json;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Exceptions;

namespace GuitoApi.Services.Auth
{
    /// <summary>
    /// Revokes the session's Google access token at Google's revoke endpoint on
    /// sign-out (issue #64): collapses the ~1h validity window of copied tokens.
    /// Never logs the token. Google revocation is idempotent — invalid_token
    /// (already expired/revoked) is success, not error.
    /// </summary>
    public class RevokeGoogleTokenService : IRevokeGoogleTokenService
    {
        public const string HttpClientName = "GoogleTokenRevoke";
        public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
        private const string AlreadyRevokedError = "invalid_token";

        private readonly IHttpClientFactory _httpClientFactory;

        public RevokeGoogleTokenService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task RevokeAsync(LogoutRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(request.AccessToken))
                throw new ProblemException(400, "accessToken is required");

            using var revokeRequest = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = request.AccessToken,
                }),
            };
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await client.SendAsync(revokeRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
                return;

            var upstream = (int)response.StatusCode;
            var (error, description) = ParseGoogleError(body);

            // Status convention: use the upstream status when the response carries
            // one (Google 4xx → same status); provider failures → 502. The error
            // body carries no credentials — its error/error_description surface
            // as the RFC 6749 JSON the UI already parses.
            if (upstream == 400 && error == AlreadyRevokedError)
                return; // already expired or already revoked — revocation is idempotent

            throw new GoogleTokenExchangeException(
                upstream is >= 400 and < 500 ? upstream : 502, error, description);
        }

        private static (string? Error, string? Description) ParseGoogleError(string body)
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                var error = json.RootElement.TryGetProperty("error", out var errorElement)
                    ? errorElement.GetString()
                    : null;
                var description = json.RootElement.TryGetProperty("error_description", out var descriptionElement)
                    ? descriptionElement.GetString()
                    : null;
                return (error, description);
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }
    }
}
