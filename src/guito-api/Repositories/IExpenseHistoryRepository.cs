using GuitoApi.Model;

namespace GuitoApi.Repositories;

public interface IExpenseHistoryRepository
{
    Task<ExpenseHistoryPage> ReadAsync(int pageSize, string? cursor, CancellationToken cancellationToken = default);
}
