namespace GuitoApi.Infrastructure.EnableBanking;

/// <summary>Result of EB POST /auth (issue #89): the redirect URL the PSU must visit.</summary>
public record EnableBankingStartAuthorization(string Url);