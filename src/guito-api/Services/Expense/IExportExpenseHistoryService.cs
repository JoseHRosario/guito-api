using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.Expense;

public interface IExportExpenseHistoryService
{
    Task<ExpenseHistory> ReadAsync(int pageSize, string? cursor, CancellationToken cancellationToken = default);
}
