using GuitoApi.DataTransferObjects.Output;
using GuitoApi.DataTransferObjects.Input;
using GuitoApi.Services.BankTransactions;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers
{
    /// <summary>Bank Transactions read + sync endpoints (issues #90, #91).</summary>
    [ApiController]
    [Route("[controller]")]
    public class BankTransactionController : ControllerBase
    {
        private readonly ISyncBankTransactionsService _syncBankTransactionsService;
        private readonly IListBankTransactionsService _listBankTransactionsService;
        private readonly IIgnoreBankTransactionService _ignoreBankTransactionService;
        private readonly IReturnBankTransactionToReviewService _returnBankTransactionToReviewService;
        private readonly IListBankExpenseSuggestionsService _listBankExpenseSuggestionsService;
        private readonly IMatchBankTransactionService _matchBankTransactionService;
        private readonly IUnmatchBankTransactionService _unmatchBankTransactionService;

        public BankTransactionController(
            ISyncBankTransactionsService syncBankTransactionsService,
            IListBankTransactionsService listBankTransactionsService,
            IIgnoreBankTransactionService ignoreBankTransactionService,
            IReturnBankTransactionToReviewService returnBankTransactionToReviewService,
            IListBankExpenseSuggestionsService listBankExpenseSuggestionsService,
            IMatchBankTransactionService matchBankTransactionService,
            IUnmatchBankTransactionService unmatchBankTransactionService)
        {
            _syncBankTransactionsService = syncBankTransactionsService;
            _listBankTransactionsService = listBankTransactionsService;
            _ignoreBankTransactionService = ignoreBankTransactionService;
            _returnBankTransactionToReviewService = returnBankTransactionToReviewService;
            _listBankExpenseSuggestionsService = listBankExpenseSuggestionsService;
            _matchBankTransactionService = matchBankTransactionService;
            _unmatchBankTransactionService = unmatchBankTransactionService;
        }

        /// <summary>All stored rows or the selected review filter, newest bookings first (#133).</summary>
        [HttpGet]
        public async Task<IReadOnlyList<BankTransaction>> ListAsync(CancellationToken cancellationToken, [FromQuery] string reviewStatus = "all")
        {
            // Binding substitutes the default for an empty query; preserve it for validation.
            return await _listBankTransactionsService.ListAsync(
                Request.Query.ContainsKey("reviewStatus") ? Request.Query["reviewStatus"].ToString() : reviewStatus, cancellationToken);
        }

        [HttpGet("{id}/suggestions")]
        public async Task<IReadOnlyList<BankTransactionLinkedExpense>> SuggestionsAsync(long id, CancellationToken cancellationToken) =>
            await _listBankExpenseSuggestionsService.ListSuggestionsAsync(id, cancellationToken);

        [HttpPost("{id}/ignore")]
        public async Task<BankTransaction> IgnoreAsync(long id, CancellationToken cancellationToken) =>
            await _ignoreBankTransactionService.IgnoreBankTransactionAsync(id, cancellationToken);

        [HttpPost("{id}/match")]
        public async Task<BankTransaction> MatchAsync(long id, BankExpenseLink request, CancellationToken cancellationToken) =>
            await _matchBankTransactionService.MatchAsync(id, request.ExpenseId, cancellationToken);

        [HttpPost("{id}/unmatch")]
        public async Task<BankTransaction> UnmatchAsync(long id, BankExpenseLink request, CancellationToken cancellationToken) =>
            await _unmatchBankTransactionService.UnmatchAsync(id, request.ExpenseId, cancellationToken);

        [HttpPost("{id}/review")]
        public async Task<BankTransaction> ReviewAsync(long id, CancellationToken cancellationToken) =>
            await _returnBankTransactionToReviewService.ReturnBankTransactionToReviewAsync(id, cancellationToken);

        /// <summary>7-day window, EB strategy=default, idempotent by sync_key (issue #90).</summary>
        [HttpPost("sync")]
        public async Task<BankSyncResult> SyncAsync(CancellationToken cancellationToken)
        {
            return await _syncBankTransactionsService.SyncAsync(cancellationToken);
        }
    }
}
