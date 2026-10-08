namespace GuitoApi.Infrastructure.EnableBanking;

/// <summary>An account authorized inside an EB session (subset of AccountResource, issue #89).</summary>
public record EnableBankingSessionAccount(string Uid, string? Iban, string Name, string Currency);