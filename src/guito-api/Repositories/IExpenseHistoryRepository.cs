using GuitoApi.Model;

namespace GuitoApi.Repositories;

public interface IExpenseHistoryRepository
{
    Task<ExpenseHistoryPage> ReadAsync(int pageSize, long offset, CancellationToken cancellationToken = default);
}
