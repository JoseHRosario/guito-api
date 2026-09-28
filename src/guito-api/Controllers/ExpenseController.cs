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
        private readonly IDeleteExpenseRowService _deleteExpenseRowService;

        public ExpenseController(ICreateExpenseService createExpenseService, 
            IListLatestExpensesService listLatestExpensesService, 
            IMatchExpensesService matchExpensesService,
            IDeleteExpenseRowService deleteExpenseRowService)
        {
            _createExpenseService = createExpenseService;
            _listLatestExpensesService = listLatestExpensesService;
            _matchExpensesService = matchExpensesService;
            _deleteExpenseRowService = deleteExpenseRowService;
        }

        [HttpPost]
        public async Task<ExpenseCreated> CreateAsync([FromBody] ExpenseCreate value, CancellationToken cancellationToken)
        {
            return await _createExpenseService.CreateAsync(value, cancellationToken);
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

        // ADR-0009: deletion is scoped to the Smoke Test tab by the route itself. It
        // deletes one row by opaque Expense Id (today the sheet row index) — see
        // IDeleteExpenseRowService. Not a general DELETE /Expense — that is rejected
        // until guito-ui needs delete/edit as a product feature.
        [HttpDelete("/Smoke/{id}")]
        public async Task DeleteSmokeAsync(int id, CancellationToken cancellationToken)
        {
            await _deleteExpenseRowService.DeleteAsync(id, cancellationToken);
        }
    }
}
