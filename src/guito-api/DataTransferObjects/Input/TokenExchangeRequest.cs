using System.ComponentModel.DataAnnotations;

namespace GuitoApi.DataTransferObjects.Input
{
    /// <summary>Body of POST /Auth/token (issue #52): the UI's Google PKCE authorization code.</summary>
    public class TokenExchangeRequest
    {
        [Required]
        public string Code { get; set; } = string.Empty;
        [Required]
        public string CodeVerifier { get; set; } = string.Empty;
        public string RedirectUri { get; set; } = string.Empty;
    }
}
