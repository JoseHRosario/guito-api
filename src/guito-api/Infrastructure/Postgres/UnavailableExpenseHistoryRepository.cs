using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Infrastructure.Postgres;

public sealed class UnavailableExpenseHistoryRepository : IExpenseHistoryRepository
{
    public Task<ExpenseHistoryPage> ReadAsync(int pageSize, string? cursor, CancellationToken cancellationToken = default) =>
        throw new ProblemException(409, "Expense history requires approved Postgres authority and is unavailable in Sheets smoke scope.");
}
