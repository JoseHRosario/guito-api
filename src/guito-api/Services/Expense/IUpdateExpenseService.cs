using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.Expense;

public interface IUpdateExpenseService
{
    Task<ExpenseDetailResponse> UpdateAsync(int id, ExpenseUpdate value, CancellationToken cancellationToken = default);
}
