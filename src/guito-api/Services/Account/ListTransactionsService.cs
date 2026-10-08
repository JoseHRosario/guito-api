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
    public class ListTransactionsService : IListTransactionsService
    {
        private readonly IEnableBankingClient _client;
        private readonly IBankAccountRepository _accounts;

        public ListTransactionsService(IEnableBankingClient client, IBankAccountRepository accounts)
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
                    throw EnableBankingFetchErrorMapper.ToProblemException(exception);
                }

                foreach (var transaction in page.Transactions)
                {
                    var detail = ToDetail(transaction);
                    if (detail is not null)
                        details.Add(detail);
                }

                continuationKey = page.ContinuationKey;
            } while (continuationKey is not null);

            return details;
        }

        /// <summary>
        /// Keeps only settled expense-direction rows (status=BOOK, DBIT — ADR-0010 keeps
        /// amounts positive at the provider boundary; PDNG/INFO and income are dropped).
        /// </summary>
        private static TransactionListDetail? ToDetail(EnableBankingTransaction transaction) =>
            transaction.Status != "BOOK" || transaction.CreditDebitIndicator != "DBIT"
                ? null
                : new TransactionListDetail
                {
                    Id = transaction.TransactionId ?? transaction.EntryReference,
                    Date = transaction.BookingDate.ToDateTime(TimeOnly.MinValue),
                    Amount = Math.Abs(transaction.Amount),
                    Description = transaction.RemittanceInformation ?? transaction.Note,
                };

    }
}