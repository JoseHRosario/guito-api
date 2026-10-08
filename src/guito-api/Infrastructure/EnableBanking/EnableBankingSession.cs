namespace GuitoApi.Infrastructure.EnableBanking;

/// <summary>
/// Result of EB POST /sessions (subset of AuthorizeSessionResponse, issue #89): the
/// session id this consent flow produced, its ASPSP, and the authorized accounts.
/// </summary>
public record EnableBankingSession(
    string SessionId,
    string AspspName,
    string AspspCountry,
    DateTime? AccessValidUntil,
    IReadOnlyList<EnableBankingSessionAccount> Accounts);