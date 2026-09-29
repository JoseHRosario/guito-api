using System.Text.Json.Serialization;

namespace GuitoApi.DataTransferObjects.Output
{
    /// <summary>RFC 6749 error body of POST /Auth/token — the shape guito-ui parses (issue #52).</summary>
    public class GoogleTokenErrorResponse
    {
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }
}
