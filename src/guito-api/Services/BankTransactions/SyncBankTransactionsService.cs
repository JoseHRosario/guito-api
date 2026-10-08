using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.EnableBanking;
using GuitoApi.Model;
using GuitoApi.Repositories;
using GuitoApi.Services.Account;

namespace GuitoApi.Services.BankTransactions
{
    /// <summary>
    /// POST /BankTransaction/sync (issue #90, ADR-0004/0013): for every linked account,
    /// fetches EB transactions with strategy=default over the fixed 7-day window, keeps
    /// only settled expense-direction rows (status=BOOK, DBIT), and stores them via
    /// IBankTransactionRepository — INSERT ... ON CONFLICT (sync_key) DO NOTHING makes
    /// re-syncs over the same window idempotent. Response is exactly {fetched, new}.
    /// </summary>
    public class SyncBankTransactionsService : ISyncBankTransactionsService
    {
        public const int WindowDays = 7;

        private readonly IEnableBankingClient _client;
        private readonly IBankAccountRepository _accounts;
        private readonly IBankTransactionRepository _transactions;

        public SyncBankTransactionsService(
            IEnableBankingClient client, IBankAccountRepository accounts, IBankTransactionRepository transactions)
        {
            _client = client;
            _accounts = accounts;
            _transactions = transactions;
        }

        public async Task<BankSyncResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            var accounts = await _accounts.ListAsync(cancellationToken);
            if (accounts.Count == 0)
                throw new BankReconnectionRequiredException("No bank account is linked yet — reconnect Enable Banking before syncing.");

            var dateTo = DateOnly.FromDateTime(DateTime.UtcNow);
            var dateFrom = dateTo.AddDays(-WindowDays);
            var result = new BankSyncResult();

            foreach (var account in accounts)
                await SyncAccountAsync(account, dateFrom, dateTo, result, cancellationToken);
            return result;
        }

        private async Task SyncAccountAsync(
            BankAccountSummary account, DateOnly dateFrom, DateOnly dateTo, BankSyncResult result,
            CancellationToken cancellationToken)
        {
            string? continuationKey = null;
            do
            {
                EnableBankingTransactionsPage page;
                try
                {
                    page = await _client.ListTransactionsAsync(account.Uid, dateFrom, dateTo, continuationKey, cancellationToken);
                }
                catch (EnableBankingApiException exception)
                {
                    throw EnableBankingFetchErrorMapper.ToProblemException(exception);
                }

                foreach (var transaction in page.Transactions)
                {
                    if (transaction.Status != "BOOK" || transaction.CreditDebitIndicator != "DBIT")
                        continue;
                    result.Fetched++;
                    var isNew = await _transactions.InsertOrSkipAsync(ToInsert(account, transaction), cancellationToken);
                    if (isNew)
                        result.New++;
                }

                continuationKey = page.ContinuationKey;
            } while (continuationKey is not null);
        }

        private static BankTransactionInsert ToInsert(BankAccountSummary account, EnableBankingTransaction transaction) => new()
        {
            AccountUid = account.Uid,
            BookingDate = transaction.BookingDate,
            Amount = Math.Abs(transaction.Amount),
            Currency = transaction.Currency,
            Direction = transaction.CreditDebitIndicator,
            RemittanceInformation = transaction.RemittanceInformation ?? transaction.Note,
            Status = transaction.Status,
            SyncKey = ComputeSyncKey(account, transaction),
        };

        /// <summary>
        /// The dedup anchor (ADR-0013): SHA-256 over EB's recommended composite —
        /// account uid + booking date + amount + credit/debit indicator + entry
        /// reference + counterparty. Re-syncs of the same row hash identically.
        /// </summary>
        public static string ComputeSyncKey(BankAccountSummary account, EnableBankingTransaction transaction)
        {
            var composite = string.Create(CultureInfo.InvariantCulture,
                $"{account.Uid}|{transaction.BookingDate:yyyy-MM-dd}|{transaction.Amount}|{transaction.CreditDebitIndicator}|{transaction.EntryReference}|{transaction.CounterpartyName}");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(composite)));
        }
    }
}