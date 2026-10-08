using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.EnableBanking;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankAuth
{
    /// <summary>
    /// Finishes the UI-driven consent flow (issue #89, ADR-0004): exchanges the auth
    /// callback's code via EB POST /sessions and upserts the authorized accounts into
    /// bank_accounts — session ids and consent state are EB-adapter internals stored
    /// with the account rows (ADR-0013 schema from #86).
    /// </summary>
    public class FinishBankAuthService : IFinishBankAuthService
    {
        private readonly IEnableBankingClient _client;
        private readonly IBankAccountRepository _accounts;

        public FinishBankAuthService(IEnableBankingClient client, IBankAccountRepository accounts)
        {
            _client = client;
            _accounts = accounts;
        }

        public async Task<BankAuthResult> FinishAsync(string code, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ProblemException(400, "The auth callback did not provide a 'code' parameter.");

            EnableBankingSession session;
            try
            {
                session = await _client.AuthorizeSessionAsync(code, cancellationToken);
            }
            catch (EnableBankingApiException exception)
            {
                throw BankAuthErrorMapper.ToProblemException(exception);
            }

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