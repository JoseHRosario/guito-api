using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.EnableBanking;
using GuitoApi.Model;
using GuitoApi.Repositories;

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
                var page = await FetchPageAsync(account, dateFrom, dateTo, continuationKey, cancellationToken);
                await StoreBookedDebitsAsync(account, page, result, cancellationToken);
                continuationKey = page.ContinuationKey;
            } while (continuationKey is not null);
        }

        private async Task<EnableBankingTransactionsPage> FetchPageAsync(
            BankAccountSummary account, DateOnly dateFrom, DateOnly dateTo,
            string? continuationKey, CancellationToken cancellationToken)
        {
            try
            {
                return await _client.ListTransactionsAsync(account.Uid, dateFrom, dateTo, continuationKey, cancellationToken);
            }
            catch (EnableBankingApiException exception)
            {
                throw EnableBankingFetchErrorMapper.ToProblemException(exception);
            }
        }

        private async Task StoreBookedDebitsAsync(
            BankAccountSummary account, EnableBankingTransactionsPage page, BankSyncResult result,
            CancellationToken cancellationToken)
        {
            foreach (var transaction in page.Transactions)
            {
                if (transaction.Status != "BOOK" || transaction.CreditDebitIndicator != "DBIT")
                    continue;
                result.Fetched++;
                var insert = ToInsert(account, transaction);
                if (await _transactions.InsertOrSkipAsync(insert, cancellationToken))
                    result.New++;
            }
        }

        private static BankTransactionInsert ToInsert(BankAccountSummary account, EnableBankingTransaction transaction) => new()
        {
            AccountUid = account.Uid,
            BookingDate = transaction.BookingDate,
            // ADR-0010: stored positive at the provider boundary — and the sync_key hashes
            // exactly this stored value, so the dedup anchor never diverges from the row.
            Amount = Math.Abs(transaction.Amount),
            Currency = transaction.Currency,
            Direction = transaction.CreditDebitIndicator,
            RemittanceInformation = transaction.RemittanceInformation ?? transaction.Note,
            Status = transaction.Status,
            SyncKey = ComputeSyncKey(account, transaction),
        };

        /// <summary>
        /// The dedup anchor (ADR-0013): SHA-256 over EB's recommended composite —
        /// account uid + booking date + amount (the stored, positive value) + credit/debit
        /// indicator + entry reference + counterparty. Re-syncs of the same row hash
        /// identically.
        /// </summary>
        private static string ComputeSyncKey(BankAccountSummary account, EnableBankingTransaction transaction)
        {
            var composite = string.Create(CultureInfo.InvariantCulture,
                $"{account.Uid}|{transaction.BookingDate:yyyy-MM-dd}|{Math.Abs(transaction.Amount)}|{transaction.CreditDebitIndicator}|{transaction.EntryReference}|{transaction.CounterpartyName}");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(composite)));
        }
    }
}