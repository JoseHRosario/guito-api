using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Services.BankTransactions;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers
{
    /// <summary>Bank Transactions sync endpoint (issue #90).</summary>
    [ApiController]
    [Route("[controller]")]
    public class BankTransactionController : ControllerBase
    {
        private readonly ISyncBankTransactionsService _syncBankTransactionsService;

        public BankTransactionController(ISyncBankTransactionsService syncBankTransactionsService)
        {
            _syncBankTransactionsService = syncBankTransactionsService;
        }

        /// <summary>7-day window, EB strategy=default, idempotent by sync_key (issue #90).</summary>
        [HttpPost("sync")]
        public async Task<BankSyncResult> SyncAsync(CancellationToken cancellationToken)
        {
            return await _syncBankTransactionsService.SyncAsync(cancellationToken);
        }
    }
}