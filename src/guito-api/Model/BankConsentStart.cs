namespace GuitoApi.Model;

/// <summary>The auth redirect URL the PSU must visit to grant consent (issue #89).</summary>
public record BankConsentStart(string Url);