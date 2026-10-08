using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankAuth
{
    /// <summary>
    /// Finishes the UI-driven consent flow (issue #89, ADR-0004): exchanges the auth
    /// callback's code via the consent provider and upserts the authorized accounts
    /// into bank_accounts — session ids and consent state are provider-adapter internals
    /// stored with the account rows (ADR-0013 schema from #86). Speaks only the
    /// IBankConsentProvider port — no EB types (issue #103); the port surfaces failures
    /// already classified as ProblemException.
    /// </summary>
    public class FinishBankAuthService : IFinishBankAuthService
    {
        private readonly IBankConsentProvider _consentProvider;
        private readonly IBankAccountRepository _accounts;

        public FinishBankAuthService(IBankConsentProvider consentProvider, IBankAccountRepository accounts)
        {
            _consentProvider = consentProvider;
            _accounts = accounts;
        }

        public async Task<BankAuthResult> FinishAsync(string code, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ProblemException(400, "The auth callback did not provide a 'code' parameter.");

            var session = await _consentProvider.FinishAuthorizationAsync(code, cancellationToken);

            foreach (var account in session.Accounts)
            {
                await _accounts.UpsertAsync(new BankAccountUpsert
                {
                    Uid = account.Uid,
                    SessionId = session.SessionId,
                    Iban = account.Iban,
                    Name = account.Name,
                    Currency = account.Currency,
                    AspspCountry = session.AspspCountry,
                    AspspName = session.AspspName,
                    ConsentStatus = "VALID",
                    ConsentExpiresAt = session.AccessValidUntil,
                }, cancellationToken);
            }

            return new BankAuthResult { AccountsLinked = session.Accounts.Count };
        }
    }
}