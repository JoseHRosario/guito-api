using GuitoApi.DataTransferObjects.Output;
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
        private readonly IListPendingBankTransactionsService _pendingBankTransactionsService;

        public BankTransactionController(
            ISyncBankTransactionsService syncBankTransactionsService,
            IListPendingBankTransactionsService pendingBankTransactionsService)
        {
            _syncBankTransactionsService = syncBankTransactionsService;
            _pendingBankTransactionsService = pendingBankTransactionsService;
        }

        /// <summary>Pending (unmatched) rows, newest bookings first (issue #91).</summary>
        [HttpGet]
        public async Task<IReadOnlyList<BankTransactionPending>> ListPendingAsync(CancellationToken cancellationToken)
        {
            return await _pendingBankTransactionsService.ListPendingAsync(cancellationToken);
        }

        /// <summary>7-day window, EB strategy=default, idempotent by sync_key (issue #90).</summary>
        [HttpPost("sync")]
        public async Task<BankSyncResult> SyncAsync(CancellationToken cancellationToken)
        {
            return await _syncBankTransactionsService.SyncAsync(cancellationToken);
        }
    }
}
