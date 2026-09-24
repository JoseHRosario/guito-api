namespace GuitoApiAuthorizer.AgentKey
{
    /// <summary>Agent keys for the X-Api-Key authorizer (issue #4/#3).</summary>
    public interface IKeysLoader
    {
        Task<IReadOnlyList<string>> LoadAsync();
    }
}