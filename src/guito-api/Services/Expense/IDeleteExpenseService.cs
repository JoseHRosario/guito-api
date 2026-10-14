namespace GuitoApi.Services.Expense;

public interface IDeleteExpenseService
{
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
