using GuitoApi.Model;

namespace GuitoApi.Repositories;

/// <summary>
/// Consent-flow port of the bank provider (issue #89, ADR-0004): start user
/// authorization (redirect URL for the SPA) and finish it (session + authorized
/// accounts). Implemented by the Enable Banking adapter in Infrastructure. Session/
/// consent ids are EB-adapter internals carried in the returned session record.
/// Throws ProblemException (400 rejected request/code / 502 provider failure).
/// </summary>
public interface IBankConsentProvider
{
    Task<BankConsentStart> StartAuthorizationAsync(
        string aspspName, string aspspCountry, string state, string redirectUrl,
        CancellationToken cancellationToken = default);

    Task<BankConsentSession> FinishAuthorizationAsync(string code, CancellationToken cancellationToken = default);
}