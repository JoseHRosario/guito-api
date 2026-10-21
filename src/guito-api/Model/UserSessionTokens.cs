namespace GuitoApi.Model;

public sealed class UserSessionTokens
{
    public string IdToken { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public string? RefreshToken { get; set; }
}
