using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.Expense;

public interface IReadExpenseDetailService
{
    Task<ExpenseDetailResponse> ReadAsync(int id, CancellationToken cancellationToken = default);
}
