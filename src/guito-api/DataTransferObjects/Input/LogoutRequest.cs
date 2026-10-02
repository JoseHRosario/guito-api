using System.ComponentModel.DataAnnotations;

namespace GuitoApi.DataTransferObjects.Input
{
    /// <summary>Body of POST /Auth/logout (issue #64): the session's Google access token to revoke.</summary>
    public class LogoutRequest
    {
        [Required]
        public string AccessToken { get; set; } = string.Empty;
    }
}
