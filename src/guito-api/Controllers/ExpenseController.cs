using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Services.Expense;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ExpenseController : ControllerBase
    {
        private readonly ICreateExpenseService _createExpenseService;
        private readonly IListLatestExpensesService _listLatestExpensesService;
        private readonly IMatchExpensesService _matchExpensesService;

        public ExpenseController(ICreateExpenseService createExpenseService, 
            IListLatestExpensesService listLatestExpensesService, 
            IMatchExpensesService matchExpensesService)
        {
            _createExpenseService = createExpenseService;
            _listLatestExpensesService = listLatestExpensesService;
            _matchExpensesService = matchExpensesService;
        }

        [HttpPost]
        public async Task CreateAsync([FromBody] ExpenseCreate value, CancellationToken cancellationToken)
        {
            await _createExpenseService.CreateAsync(value, cancellationToken);
        }

        [HttpGet("latest/{count}")]
        public async Task<ExpenseListLatest> ListLatestAsync(int count, CancellationToken cancellationToken)
        {
            return await _listLatestExpensesService.ListLatestAsync(count, cancellationToken);
        }

        [HttpGet("match")]
        [ResponseCache(Duration = 120, Location = ResponseCacheLocation.Any)]
        public async Task<ExpenseMatchList> MatchExpensesAsync(CancellationToken cancellationToken)
        {
            return await _matchExpensesService.MatchExpensesAsync(cancellationToken);
        }
    }
}
