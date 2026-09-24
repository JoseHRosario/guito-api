namespace GuitoApiAuthorizer.GoogleToken
{
    /// <summary>A single RSA signing key from Google's JWKS document.</summary>
    public record JsonWebKey(string Kid, string N, string E);
}