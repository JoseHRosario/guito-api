using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Services.Account;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class AccountController : ControllerBase
    {
        private readonly IListTransactionsService _listTransactionsService;

        public AccountController(IListTransactionsService listTransactionsService)
        {
            _listTransactionsService = listTransactionsService;
        }

        [HttpGet("transactions")]
        [ResponseCache(Duration = 120, Location = ResponseCacheLocation.Any)]
        public async Task<TransactionList> ListTransactions(DateTime? dateFrom, DateTime? dateTo)
        {
            return await _listTransactionsService.List(dateFrom, dateTo);
        }
    }
}
