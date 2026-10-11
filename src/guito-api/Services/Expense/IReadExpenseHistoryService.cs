using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.Expense;

public interface IReadExpenseHistoryService
{
    Task<ExpenseHistory> ReadAsync(int pageSize, int pageIndex, CancellationToken cancellationToken = default);
}
