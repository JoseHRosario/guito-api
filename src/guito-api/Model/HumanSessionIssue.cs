namespace GuitoApi.Model;

public sealed class HumanSessionIssue(string handle, HumanSessionTokens tokens, DateTimeOffset expiresAt)
{
    public string Handle { get; } = handle;
    public HumanSessionTokens Tokens { get; } = tokens;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}
