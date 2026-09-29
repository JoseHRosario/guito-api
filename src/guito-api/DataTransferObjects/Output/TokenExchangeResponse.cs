namespace GuitoApi.DataTransferObjects.Output
{
    /// <summary>Same token payload the UI expects from Google's token endpoint (issue #52).</summary>
    public class TokenExchangeResponse
    {
        public string? IdToken { get; set; }
        public string? AccessToken { get; set; }
        public long ExpiresIn { get; set; }
    }
}
