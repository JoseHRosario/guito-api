using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankTransactions
{
    /// <summary>
    /// POST /BankTransaction/sync (issue #90, ADR-0004/0013): for every linked account,
    /// fetches provider transactions over the fixed 7-day window with continuation-key
    /// pagination, keeps only settled expense-direction rows (status=BOOK, DBIT), and
    /// stores them via IBankTransactionRepository — INSERT ... ON CONFLICT (sync_key)
    /// DO NOTHING makes re-syncs over the same window idempotent. Response is exactly
    /// {fetched, new}. Speaks only provider ports (issue #103) — no Enable Banking types.
    /// Issue #112 (ADR-0014): asks Jev (existing ResolveCategoryAsync decision path)
    /// for a category suggestion per NEW row, against the categories seeded by the
    /// migration script (004) — fail-open: a Jev failure or an unresolvable choice
    /// stores no suggestion, never a silent default.
    /// </summary>
    public class SyncBankTransactionsService : ISyncBankTransactionsService
    {
        public const int WindowDays = 7;

        /// <summary>Max concurrent Jev suggestion calls (the Decisions API is one state per request — ADR-0014).</summary>
        public const int SuggestionConcurrency = 4;

        private readonly IBankTransactionProvider _transactionsProvider;
        private readonly IBankAccountRepository _accounts;
        private readonly IBankTransactionRepository _transactions;
        private readonly ICategoriesRepository _categories;
        private readonly IExpenseExtractionRepository _extraction;

        public SyncBankTransactionsService(
            IBankTransactionProvider transactionsProvider, IBankAccountRepository accounts,
            IBankTransactionRepository transactions, ICategoriesRepository categories,
            IExpenseExtractionRepository extraction)
        {
            _transactionsProvider = transactionsProvider;
            _accounts = accounts;
            _transactions = transactions;
            _categories = categories;
            _extraction = extraction;
        }

        public async Task<BankSyncResult> SyncAsync(CancellationToken cancellationToken = default)
        {
            var accounts = await _accounts.ListAsync(cancellationToken);
            if (accounts.Count == 0)
                throw new BankReconnectionRequiredException("No bank account is linked yet — reconnect Enable Banking before syncing.");

            var categories = await _categories.ListAsync(cancellationToken);
            var dateTo = DateOnly.FromDateTime(DateTime.UtcNow);
            var dateFrom = dateTo.AddDays(-WindowDays);
            var result = new BankSyncResult();
            var newTransactions = new List<(string SyncKey, string? RemittanceInformation)>();

            foreach (var account in accounts)
                await SyncAccountAsync(account, dateFrom, dateTo, result, newTransactions, cancellationToken);

            await SuggestCategoriesAsync(newTransactions, categories, cancellationToken);
            return result;
        }

        private async Task SyncAccountAsync(
            BankAccountSummary account, DateOnly dateFrom, DateOnly dateTo, BankSyncResult result,
            List<(string SyncKey, string? RemittanceInformation)> newTransactions, CancellationToken cancellationToken)
        {
            string? continuationKey = null;
            do
            {
                // The port surfaces failures already classified as ProblemException (409/429/502).
                var page = await _transactionsProvider.ListTransactionsAsync(account.Uid, dateFrom, dateTo, continuationKey, cancellationToken);
                await StoreBookedDebitsAsync(account, page, result, newTransactions, cancellationToken);
                continuationKey = page.ContinuationKey;
            } while (continuationKey is not null);
        }

        private async Task StoreBookedDebitsAsync(
            BankAccountSummary account, BankTransactionSourcePage page, BankSyncResult result,
            List<(string SyncKey, string? RemittanceInformation)> newTransactions, CancellationToken cancellationToken)
        {
            foreach (var transaction in page.Transactions)
            {
                if (transaction.Status != "BOOK" || transaction.CreditDebitIndicator != "DBIT")
                    continue;
                result.Fetched++;
                var insert = ToInsert(account, transaction);
                if (await _transactions.InsertOrSkipAsync(insert, cancellationToken))
                {
                    result.New++;
                    // Only NEW rows get a suggestion — re-syncs never re-suggest (ADR-0014).
                    newTransactions.Add((insert.SyncKey, insert.RemittanceInformation));
                }
            }
        }

        /// <summary>
        /// Resolves a category suggestion per new row through the Jev choice path,
        /// bounded-concurrency (one state per Decisions request). Fail-open: any Jev
        /// failure or an unresolvable choice leaves the row's suggestion NULL.
        /// </summary>
        private async Task SuggestCategoriesAsync(
            List<(string SyncKey, string? RemittanceInformation)> newTransactions,
            IReadOnlyList<CategorySummary> categories, CancellationToken cancellationToken)
        {
            if (newTransactions.Count == 0 || categories.Count == 0)
                return;

            var idsByName = categories.ToDictionary(c => c.Name, c => c.Id);
            var candidates = categories.Select(c => new CategoryListDetail { Name = c.Name }).ToList();
            using var gate = new SemaphoreSlim(SuggestionConcurrency);
            var suggestions = await Task.WhenAll(newTransactions.Select(transaction => SuggestOneAsync(
                transaction, idsByName, candidates, gate, cancellationToken)));
            foreach (var suggestion in suggestions.Where(s => s.CategoryId is not null))
                await _transactions.UpdateSuggestedCategoryAsync(suggestion.SyncKey, suggestion.CategoryId, cancellationToken);
        }

        private async Task<(string SyncKey, long? CategoryId)> SuggestOneAsync(
            (string SyncKey, string? RemittanceInformation) transaction,
            Dictionary<string, long> idsByName, IReadOnlyList<CategoryListDetail> candidates,
            SemaphoreSlim gate, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(transaction.RemittanceInformation))
                return (transaction.SyncKey, null);

            await gate.WaitAsync(cancellationToken);
            try
            {
                var choice = await _extraction.ResolveCategoryAsync(
                    transaction.RemittanceInformation, string.Empty, candidates, cancellationToken);
                return (transaction.SyncKey, idsByName.GetValueOrDefault(choice));
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested is false)
            {
                // Fail-open (ADR-0014): a Jev failure never fails the sync.
                return (transaction.SyncKey, null);
            }
            finally
            {
                gate.Release();
            }
        }

        private static BankTransactionInsert ToInsert(BankAccountSummary account, BankTransactionSource transaction) => new()
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
        /// The dedup anchor (ADR-0013): SHA-256 over the provider's recommended composite —
        /// account uid + booking date + amount (the stored, positive value) + credit/debit
        /// indicator + entry reference + counterparty. Re-syncs of the same row hash
        /// identically.
        /// </summary>
        private static string ComputeSyncKey(BankAccountSummary account, BankTransactionSource transaction)
        {
            var composite = string.Create(CultureInfo.InvariantCulture,
                $"{account.Uid}|{transaction.BookingDate:yyyy-MM-dd}|{Math.Abs(transaction.Amount)}|{transaction.CreditDebitIndicator}|{transaction.EntryReference}|{transaction.CounterpartyName}");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(composite)));
        }
    }
}
