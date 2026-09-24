namespace GuitoApiAuthorizer.GoogleToken
{
    /// <summary>Claims extracted from a verified Google ID token.</summary>
    public record GoogleTokenClaims(string Email, string Audience, string Issuer, long ExpiresAtSeconds);
}