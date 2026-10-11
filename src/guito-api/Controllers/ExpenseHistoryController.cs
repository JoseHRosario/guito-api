using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Services.Expense;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers;

[ApiController]
[Route("Expense/history")]
public sealed class ExpenseHistoryController(IExportExpenseHistoryService service) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<ExpenseHistory> ReadAsync([FromQuery] ExpenseHistoryQuery query,
        CancellationToken cancellationToken) => service.ReadAsync(query.PageSize, query.Cursor, cancellationToken);
}
