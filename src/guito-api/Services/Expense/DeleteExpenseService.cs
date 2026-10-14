using GuitoApi.Repositories;

namespace GuitoApi.Services.Expense;

public sealed class DeleteExpenseService(IExpenseDetailRepository repository, IExpenseRevisionResolver revision) : IDeleteExpenseService
{
    public Task DeleteAsync(int id, CancellationToken cancellationToken = default) =>
        repository.DeleteAsync(id, revision.RequireRevision(), cancellationToken);
}
