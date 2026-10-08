namespace GuitoApi.Model;

/// <summary>An account authorized by a consent (issue #89).</summary>
public record BankConsentAccount(string Uid, string? Iban, string Name, string Currency);