namespace GuitoApiAuthorizer.GoogleToken
{
    /// <summary>Verifies Google ID tokens; test seam for stub validators.</summary>
    public interface IGoogleTokenValidator
    {
        Task<GoogleTokenResult> ValidateAsync(string? idToken, CancellationToken cancellationToken = default);
    }
}