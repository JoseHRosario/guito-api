using GuitoApi.Repositories;

namespace GuitoApi.Services.Expense;

public sealed class DeletePersonalExpenseService(IExpenseDetailRepository repository, IExpenseRevisionResolver revision) : IDeletePersonalExpenseService
{
    public Task DeleteAsync(int id, CancellationToken cancellationToken = default) =>
        repository.DeleteAsync(id, revision.RequireRevision(), cancellationToken);
}
