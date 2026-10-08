using System.Text.Json;

namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>
    /// Enable Banking API returned an error (issue #89). Carries the upstream status and
    /// message so callers can classify (re-auth vs rate limit vs provider failure).
    /// </summary>
    public class EnableBankingApiException : Exception
    {
        public int StatusCode { get; }

        public EnableBankingApiException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }

        /// <summary>Parses the EB ErrorResponse shape ({status, message, details}); falls back to the raw body.</summary>
        public static EnableBankingApiException FromResponse(EnableBankingApiResponse response)
        {
            string message;
            try
            {
                var document = JsonDocument.Parse(response.Body);
                message = document.RootElement.TryGetProperty("message", out var property)
                    ? property.GetString() ?? response.Body
                    : response.Body;
            }
            catch (JsonException)
            {
                message = response.Body;
            }

            return new EnableBankingApiException(response.StatusCode, $"Enable Banking API error ({response.StatusCode}): {message}");
        }
    }
}