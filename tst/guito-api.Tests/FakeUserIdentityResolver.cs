using GuitoApi.Model;
using GuitoApi.Services;

namespace GuitoApi.Tests;

/// <summary>
/// Fixed-identity resolver for direct repository unit tests: every write is
/// attributed to this constant creator email.
/// </summary>
public class FakeUserIdentityResolver : IUserIdentityResolver
{
    public const string Email = "agent@example.com";

    public UserIdentity ResolveUserIdentity() => new() { Email = Email };

    public string GetEmail() => Email;
}
