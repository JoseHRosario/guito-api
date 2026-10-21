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
    /// stores them via IBankTransactionRepository with guarded reference refreshes and
    /// conservative occurrence slots for ambiguous observations. Response is exactly
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
            var movements = new List<BankTransactionSource>();
            do
            {
                var page = await _transactionsProvider.ListTransactionsAsync(account.FetchUid ?? account.Uid, dateFrom, dateTo, continuationKey, cancellationToken);
                movements.AddRange(page.Transactions.Where(t => t.Status == "BOOK" && t.CreditDebitIndicator == "DBIT"));
                continuationKey = page.ContinuationKey;
            } while (continuationKey is not null);
            await StoreBookedDebitsAsync(account, movements, result, newTransactions, cancellationToken);
        }

        private async Task StoreBookedDebitsAsync(
            BankAccountSummary account, IReadOnlyList<BankTransactionSource> movements, BankSyncResult result,
            List<(string SyncKey, string? RemittanceInformation)> newTransactions, CancellationToken cancellationToken)
        {
            var referenceCounts = movements.Where(t => !string.IsNullOrWhiteSpace(t.EntryReference))
                .GroupBy(t => t.EntryReference!).ToDictionary(group => group.Key, group => group.Count());
            var occurrences = new Dictionary<string, int>();
            foreach (var transaction in movements)
            {
                result.Fetched++;
                var insert = Identify(account, transaction, referenceCounts, occurrences);
                var imported = await _transactions.ImportAsync(insert, cancellationToken);
                if (imported.IsNew)
                {
                    result.New++;
                    // Only NEW rows get a suggestion — re-syncs never re-suggest (ADR-0014).
                    newTransactions.Add((imported.SyncKey, insert.RemittanceInformation));
                }
            }
        }

        private static BankTransactionInsert Identify(BankAccountSummary account, BankTransactionSource transaction,
            IReadOnlyDictionary<string, int> referenceCounts, Dictionary<string, int> occurrences)
        {
            var fingerprint = Fingerprint(account, transaction);
            var occurrence = occurrences.GetValueOrDefault(fingerprint) + 1;
            occurrences[fingerprint] = occurrence;
            var reliable = !string.IsNullOrWhiteSpace(account.IdentificationHash) && !string.IsNullOrWhiteSpace(transaction.EntryReference)
                && referenceCounts[transaction.EntryReference] == 1;
            return ToInsert(account, transaction, reliable, $"ambiguous:{fingerprint}:{occurrence}");
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
            using var gate = new SemaphoreSlim(SuggestionConcurrency);
            var suggestions = await Task.WhenAll(newTransactions.Select(transaction => SuggestOneAsync(
                transaction, idsByName, categories, gate, cancellationToken)));
            foreach (var suggestion in suggestions.Where(s => s.CategoryId is not null))
                await _transactions.UpdateSuggestedCategoryAsync(suggestion.SyncKey, suggestion.CategoryId, cancellationToken);
        }

        private async Task<(string SyncKey, long? CategoryId)> SuggestOneAsync(
            (string SyncKey, string? RemittanceInformation) transaction,
            Dictionary<string, long> idsByName, IReadOnlyList<CategorySummary> candidates,
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

        private static BankTransactionInsert ToInsert(BankAccountSummary account, BankTransactionSource transaction, bool reliable, string fallbackKey) => new()
        {
            AccountUid = account.Uid,
            BookingDate = transaction.BookingDate,
            // Only settled debits reach storage; normalize the provider sign (ADR-0010).
            Amount = Math.Abs(transaction.Amount),
            Currency = transaction.Currency,
            Direction = transaction.CreditDebitIndicator,
            RemittanceInformation = transaction.RemittanceInformation ?? transaction.Note,
            Status = transaction.Status,
            SyncKey = reliable ? Hash(["reference", account.Uid, transaction.EntryReference]) : fallbackKey,
            FallbackSyncKey = fallbackKey,
            IdentityReliable = reliable,
            EntryReference = string.IsNullOrWhiteSpace(transaction.EntryReference) ? null : transaction.EntryReference,
            TransactionDate = transaction.TransactionDate,
            ValueDate = transaction.ValueDate,
        };

        // An ambiguous fingerprint is an observation slot, NOT proof of movement identity.
        // Occurrence ordinals preserve identical purchases across all pages of this fetch.
        private static string Fingerprint(BankAccountSummary account, BankTransactionSource transaction) => Hash([
            "observation", account.Uid, transaction.EntryReference, transaction.BookingDate.ToString("yyyy-MM-dd"),
            transaction.TransactionDate?.ToString("yyyy-MM-dd"), transaction.ValueDate?.ToString("yyyy-MM-dd"),
            Math.Abs(transaction.Amount).ToString("G29", CultureInfo.InvariantCulture), transaction.Currency,
            transaction.CreditDebitIndicator, transaction.RemittanceInformation, transaction.Note, transaction.CounterpartyName]);

        private static string Hash(string?[] components) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(components))));
    }
}
