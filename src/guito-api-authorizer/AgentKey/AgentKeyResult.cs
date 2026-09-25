namespace GuitoApiAuthorizer.AgentKey
{
    /// <summary>Outcome of an X-Api-Key validation attempt (fail-closed).</summary>
    public record AgentKeyResult(bool Valid, string? FailureReason);
}