using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.EnableBanking;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Account
{
    /// <summary>
    /// Enable Banking adapter behind IListTransactionsService (issue #89, ADR-0004):
    /// reads the linked accounts (bank_accounts, #89 consent flow), fetches every
    /// account's transactions with strategy=default and continuation_key pagination,
    /// and keeps only settled expense-direction rows (status=BOOK, DBIT) with amounts
    /// normalized positive at the provider boundary (ADR-0010). Fetched rows are NOT
    /// stored here — that is the sync endpoint's job (#90).
    /// </summary>
    public class ListTransactionsEnableBankingService : IListTransactionsService
    {
        private readonly IEnableBankingClient _client;
        private readonly IBankAccountRepository _accounts;

        public ListTransactionsEnableBankingService(IEnableBankingClient client, IBankAccountRepository accounts)
        {
            _client = client;
            _accounts = accounts;
        }

        public async Task<TransactionList> ListAsync(DateTime? dateFrom = null, DateTime? dateTo = null,
            CancellationToken cancellationToken = default)
        {
            var output = new TransactionList();
            var accounts = await _accounts.ListAsync(cancellationToken);
            if (accounts.Count == 0)
                throw new BankReconnectionRequiredException("No bank account is linked yet — reconnect Enable Banking to fetch transactions.");

            foreach (var account in accounts)
                output.Transactions.AddRange(await FetchAccountAsync(account, dateFrom, dateTo, cancellationToken));
            return output;
        }

        private async Task<List<TransactionListDetail>> FetchAccountAsync(
            Model.BankAccountSummary account, DateTime? dateFrom, DateTime? dateTo, CancellationToken cancellationToken)
        {
            var details = new List<TransactionListDetail>();
            DateOnly? windowFrom = dateFrom is null ? null : DateOnly.FromDateTime(dateFrom.Value);
            DateOnly? windowTo = dateTo is null ? null : DateOnly.FromDateTime(dateTo.Value);

            // continuation_key pagination until null (ADR-0004/#89).
            string? continuationKey = null;
            do
            {
                EnableBankingTransactionsPage page;
                try
                {
                    page = await _client.ListTransactionsAsync(account.Uid, windowFrom, windowTo, continuationKey, cancellationToken);
                }
                catch (EnableBankingApiException exception)
                {
                    throw ClassifyException(exception);
                }

                foreach (var transaction in page.Transactions)
                {
                    if (transaction.Status != "BOOK" || transaction.CreditDebitIndicator != "DBIT")
                        continue;
                    details.Add(new TransactionListDetail
                    {
                        Id = transaction.TransactionId ?? transaction.EntryReference,
                        Date = transaction.BookingDate.ToDateTime(TimeOnly.MinValue),
                        Amount = Math.Abs(transaction.Amount),
                        Description = transaction.RemittanceInformation ?? transaction.Note,
                    });
                }

                continuationKey = page.ContinuationKey;
            } while (continuationKey is not null);

            return details;
        }

        /// <summary>
        /// Upstream EB failures classify per ADR-0004: session lost/expired or unknown
        /// session → 409 reconnect; rate limit (429, PSD2 ~4 accesses/day per account)
        /// → 429 with the upstream wording; anything else is a provider failure (502).
        /// </summary>
        private static ProblemException ClassifyException(EnableBankingApiException exception) =>
            exception.StatusCode switch
            {
                401 or 404 => new BankReconnectionRequiredException(
                    "The bank connection has expired — reconnect Enable Banking to fetch transactions."),
                429 => new ProblemException(429,
                    "The bank is rate-limiting transaction fetches (PSD2 limits) — try again later."),
                _ => new ProblemException(502,
                    $"Enable Banking request failed: {exception.Message}"),
            };
    }
}