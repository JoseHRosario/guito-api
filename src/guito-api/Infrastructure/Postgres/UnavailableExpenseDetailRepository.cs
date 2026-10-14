using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Infrastructure.Postgres;

public sealed class UnavailableExpenseDetailRepository : IExpenseDetailRepository
{
    public Task<ExpenseDetail> UpdateAsync(int id, long revision, ExpenseEdit value, CancellationToken cancellationToken = default) =>
        throw new ProblemException(409, "Expense edit requires personal Postgres authority; smoke scope is unavailable.");

    public Task DeleteAsync(int id, long revision, CancellationToken cancellationToken = default) =>
        throw new ProblemException(409, "Expense deletion requires personal Postgres authority; smoke scope is unavailable.");

    public Task<ExpenseDetail> ReadAsync(int id, CancellationToken cancellationToken = default) =>
        throw new ProblemException(409, "Expense detail requires personal Postgres authority; smoke scope is unavailable.");
}
