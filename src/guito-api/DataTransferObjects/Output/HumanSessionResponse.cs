namespace GuitoApi.DataTransferObjects.Output;

public sealed class HumanSessionResponse
{
    public string IdToken { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public int ExpiresIn { get; init; }
    public DateTimeOffset SessionExpiresAt { get; init; }
}
