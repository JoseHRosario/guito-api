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
        private readonly IListExpensesService _listExpensesService;
        private readonly ICreateExpenseService _createExpenseService;
        private readonly IListLatestExpensesService _listLatestExpensesService;
        private readonly IMatchExpensesService _matchExpensesService;
        private readonly IDeleteExpenseRowService _deleteExpenseRowService;
        private readonly IReadExpenseHistoryService _readExpenseHistoryService;

        public ExpenseController(ICreateExpenseService createExpenseService, 
            IListLatestExpensesService listLatestExpensesService, 
            IMatchExpensesService matchExpensesService,
            IDeleteExpenseRowService deleteExpenseRowService,
            IListExpensesService listExpensesService,
            IReadExpenseHistoryService readExpenseHistoryService)
        {
            _listExpensesService = listExpensesService;
            _createExpenseService = createExpenseService;
            _listLatestExpensesService = listLatestExpensesService;
            _matchExpensesService = matchExpensesService;
            _deleteExpenseRowService = deleteExpenseRowService;
            _readExpenseHistoryService = readExpenseHistoryService;
        }

        [HttpGet]
        public Task<ExpenseListLatest> ListAsync([FromQuery] string? category, [FromQuery] string? month,
            CancellationToken cancellationToken) => _listExpensesService.ListAsync(category, month, cancellationToken);

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

        [HttpGet("history")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<ExpenseHistory> HistoryAsync(CancellationToken cancellationToken,
            [FromQuery] int pageSize = 100, [FromQuery] int pageIndex = 0)
        {
            return await _readExpenseHistoryService.ReadAsync(pageSize, pageIndex, cancellationToken);
        }

        [HttpGet("match")]
        [ResponseCache(Duration = 120, Location = ResponseCacheLocation.Any)]
        public async Task<ExpenseMatchList> MatchExpensesAsync(CancellationToken cancellationToken)
        {
            return await _matchExpensesService.MatchExpensesAsync(cancellationToken);
        }

        // Not a general expense-delete endpoint: cleanup always targets the Sheets smoke tab,
        // independently of headers (ADR-0015).
        [HttpDelete("/Smoke/{id}")]
        public async Task DeleteSmokeAsync(int id, CancellationToken cancellationToken)
        {
            await _deleteExpenseRowService.DeleteAsync(id, cancellationToken);
        }
    }
}
