namespace GuitoApiAuthorizer.GoogleToken
{
    /// <summary>Outcome of a Google ID-token validation attempt (fail-closed: Valid ⇒ Claims set).</summary>
    public record GoogleTokenResult(bool Valid, string? FailureReason, GoogleTokenClaims? Claims);
}