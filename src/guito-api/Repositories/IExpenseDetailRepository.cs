using GuitoApi.Model;

namespace GuitoApi.Repositories;

public interface IExpenseDetailRepository
{
    Task<ExpenseDetail> ReadAsync(int id, CancellationToken cancellationToken = default);
    Task<ExpenseDetail> UpdateAsync(int id, long revision, ExpenseEdit value, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, long revision, CancellationToken cancellationToken = default);
}
