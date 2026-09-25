namespace GuitoApiAuthorizer.AgentKey
{
    /// <summary>Validates a provided X-Api-Key against loaded agent keys; test seam.</summary>
    public interface IAgentKeyValidator
    {
        Task<AgentKeyResult> ValidateAsync(string? providedKey, CancellationToken cancellationToken = default);
    }
}