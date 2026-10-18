namespace GuitoApi.Model;

public sealed record HumanSession(string HandleHash, string Subject, string Email, string EncryptedCredential, DateTimeOffset ExpiresAt);
