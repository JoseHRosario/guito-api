using System.Security.Cryptography;
using System.Text;

namespace GuitoApi.Services.Auth;

public static class UserSessionHandle
{
    public static string Create() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string? Hash(string? handle) => handle is { Length: 64 } && handle.All(char.IsAsciiHexDigit)
        ? Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(handle))) : null;
}
