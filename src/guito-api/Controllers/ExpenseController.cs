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
        private readonly IReadExpenseDetailService _readExpenseDetailService;
        private readonly IUpdateExpenseService _updateExpenseService;
        private readonly IDeletePersonalExpenseService _deletePersonalExpenseService;

        public ExpenseController(ICreateExpenseService createExpenseService, 
            IListLatestExpensesService listLatestExpensesService, 
            IMatchExpensesService matchExpensesService,
            IDeleteExpenseRowService deleteExpenseRowService,
            IListExpensesService listExpensesService,
            IReadExpenseHistoryService readExpenseHistoryService,
            IReadExpenseDetailService readExpenseDetailService, IUpdateExpenseService updateExpenseService,
            IDeletePersonalExpenseService deletePersonalExpenseService)
        {
            _listExpensesService = listExpensesService;
            _createExpenseService = createExpenseService;
            _listLatestExpensesService = listLatestExpensesService;
            _matchExpensesService = matchExpensesService;
            _deleteExpenseRowService = deleteExpenseRowService;
            _readExpenseHistoryService = readExpenseHistoryService;
            _readExpenseDetailService = readExpenseDetailService;
            _updateExpenseService = updateExpenseService;
            _deletePersonalExpenseService = deletePersonalExpenseService;
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

        [HttpGet("{id}")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<ExpenseDetailResponse> DetailAsync(int id, CancellationToken cancellationToken)
        {
            var detail = await _readExpenseDetailService.ReadAsync(id, cancellationToken);
            Response.Headers.ETag = $"\"{detail.Revision}\"";
            return detail;
        }

        [HttpPut("{id}")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<ExpenseDetailResponse> UpdateAsync(int id, [FromBody] ExpenseUpdate value, CancellationToken cancellationToken)
        {
            var detail = await _updateExpenseService.UpdateAsync(id, value, cancellationToken);
            Response.Headers.ETag = $"\"{detail.Revision}\"";
            return detail;
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeletePersonalAsync(int id, CancellationToken cancellationToken)
        {
            await _deletePersonalExpenseService.DeleteAsync(id, cancellationToken);
            return NoContent();
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
