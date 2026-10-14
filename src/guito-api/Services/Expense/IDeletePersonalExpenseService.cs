namespace GuitoApi.Services.Expense;

public interface IDeletePersonalExpenseService
{
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
