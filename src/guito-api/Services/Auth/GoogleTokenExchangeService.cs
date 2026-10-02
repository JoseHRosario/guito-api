using System.Net;
using System.Text.Json;
using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.Secrets;
using GuitoApi.Services;
using Microsoft.Extensions.Options;

namespace GuitoApi.Services.Auth
{
    /// <summary>
    /// Exchanges the UI's Google PKCE code at Google's token endpoint (issue #52):
    /// adds client_secret (human-auth secret) and client_id, returns the token set.
    /// Never logs the secret or the tokens.
    /// </summary>
    public class GoogleTokenExchangeService : ITokenExchangeService
    {
        public const string HttpClientName = "GoogleTokenExchange";
        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AppConfigurationOptions _options;
        private readonly IHumanAuthSecretProvider _humanAuthSecrets;

        public GoogleTokenExchangeService(
            IHttpClientFactory httpClientFactory,
            IOptions<AppConfigurationOptions> options,
            IHumanAuthSecretProvider humanAuthSecrets)
        {
            _httpClientFactory = httpClientFactory;
            _options = options.Value;
            _humanAuthSecrets = humanAuthSecrets;
        }

        public async Task<TokenExchangeResponse> ExchangeAsync(TokenExchangeRequest request, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.CodeVerifier))
                throw new ProblemException(400, "code and codeVerifier are required");

            var clientId = _options.Authentication.GoogleClientId;
            if (string.IsNullOrEmpty(clientId))
                throw new ProblemException(500, "Google OAuth client is not configured");

            var secret = (await _humanAuthSecrets.GetAsync(cancellationToken)).ClientSecret;
            if (string.IsNullOrEmpty(secret))
                throw new ProblemException(500, "Google OAuth client secret is not configured");

            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = request.Code,
                    ["code_verifier"] = request.CodeVerifier,
                    ["redirect_uri"] = request.RedirectUri,
                    ["client_id"] = clientId,
                    ["client_secret"] = secret,
                }),
            };
            using var client = _httpClientFactory.CreateClient(HttpClientName);
            var response = await client.SendAsync(tokenRequest, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // Status convention: use the upstream status when the response carries
            // one (Google 4xx → same status); provider failures → 502. The error
            // body carries no credentials — its error/error_description is safe
            // to surface so the UI can diagnose.
            if (!response.IsSuccessStatusCode)
            {
                // Status convention: use the upstream status when the response carries
                // one (Google 4xx → same status); provider failures → 502. The error
                // body carries no credentials — its error/error_description surface
                // as the RFC 6749 JSON the UI already parses.
                var upstream = (int)response.StatusCode;
                var (error, description) = ParseGoogleError(body);
                throw new GoogleTokenExchangeException(
                    upstream is >= 400 and < 500 ? upstream : 502, error, description);
            }

            return JsonSerializer.Deserialize<TokenExchangeResponse>(
                body,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                })
                ?? throw new ProblemException(502, "Google token response is invalid");
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
