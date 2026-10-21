namespace GuitoApi.Model;

public sealed record UserSession(string HandleHash, string Subject, string Email, string EncryptedCredential, DateTimeOffset ExpiresAt);
