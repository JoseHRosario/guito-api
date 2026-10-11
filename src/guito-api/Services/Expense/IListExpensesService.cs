using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.Expense;

public interface IListExpensesService
{
    Task<ExpenseListLatest> ListAsync(string? category, string? month, CancellationToken cancellationToken = default);
}
