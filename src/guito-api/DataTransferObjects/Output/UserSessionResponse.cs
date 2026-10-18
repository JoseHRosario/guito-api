namespace GuitoApi.DataTransferObjects.Output;

public sealed class UserSessionResponse
{
    public string IdToken { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public int ExpiresIn { get; init; }
    public DateTimeOffset SessionExpiresAt { get; init; }
}
