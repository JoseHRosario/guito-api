namespace GuitoApi.Model;

public sealed class UserSessionIssue(string handle, UserSessionTokens tokens, DateTimeOffset expiresAt)
{
    public string Handle { get; } = handle;
    public UserSessionTokens Tokens { get; } = tokens;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}
