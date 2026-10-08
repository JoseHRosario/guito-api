namespace GuitoApi.Model;

/// <summary>
/// The outcome of finishing a consent flow: the session id (EB-adapter internal,
/// stored with the account rows), its ASPSP, and the authorized accounts.
/// </summary>
public record BankConsentSession(
    string SessionId,
    string AspspName,
    string AspspCountry,
    DateTime? AccessValidUntil,
    IReadOnlyList<BankConsentAccount> Accounts);