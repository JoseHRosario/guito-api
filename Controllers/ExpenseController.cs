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

        public ExpenseController(ICreateExpenseService createExpenseService, IListLatestExpensesService listLatestExpensesService)
        {
            _createExpenseService = createExpenseService;
            _listLatestExpensesService = listLatestExpensesService;
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
    }
}
