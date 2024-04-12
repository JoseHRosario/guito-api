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
        public async Task Create([FromBody] ExpenseCreate value)
        {
            await _createExpenseService.Create(value);
        }

        [HttpGet("latest/{count}")]
        public async Task<ExpenseListLatest> ListLatest(int count)
        {
            return await _listLatestExpensesService.ListLatest(count);
        }

        [HttpGet("match")]
        public async Task<ExpenseMatchList> MatchExpenses()
        {
            return await _matchExpensesService.MatchExpenses();
        }
    }
}
