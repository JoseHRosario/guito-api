using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Infrastructure.Postgres;

// Reject only the history operation; resolving the shared controller must leave other routes usable.
public sealed class UnavailableExpenseHistoryRepository(Exception exception) : IExpenseHistoryRepository
{
    public Task<ExpenseHistoryPage> ReadAsync(int pageSize, long offset,
        CancellationToken cancellationToken = default) => throw exception;
}
