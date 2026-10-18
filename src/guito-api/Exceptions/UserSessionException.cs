namespace GuitoApi.Exceptions;

public sealed class UserSessionException(int statusCode, string error, string description) : Exception(description)
{
    public int StatusCode { get; } = statusCode;
    public string Error { get; } = error;
    public static UserSessionException Expired() => new(401, "session_expired", "Sign in again to continue.");
    public static UserSessionException Unavailable() => new(503, "temporarily_unavailable", "Session renewal is temporarily unavailable. Please retry.");
}
