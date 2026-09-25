namespace GuitoApiAuthorizer.GoogleToken
{
    /// <summary>Serves Google's public signing keys (JWKS) for ID-token verification.</summary>
    public interface IJwksClient
    {
        Task<IReadOnlyList<JsonWebKey>> GetKeysAsync(CancellationToken cancellationToken = default);
    }
}