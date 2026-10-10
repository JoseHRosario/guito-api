using GuitoApi.Repositories;

namespace GuitoApi.Services.Expense
{
    /// <summary>
    /// Adapter over <see cref="IExpenseRepository"/> (repository-pattern refactor,
    /// ADR 0011): keeps the IDeleteExpenseRowService contract so controllers and
    /// the wire shape are unchanged, while every Sheets behavior moved into the
    /// repository layer. Not-found now surfaces as ExpenseNotFoundException from
    /// the repository, translated to 404 by the exception handler.
    /// </summary>
    public class DeleteExpenseService : IDeleteExpenseRowService
    {
        private readonly IExpenseRepository _expenseRepository;

        public DeleteExpenseService(IExpenseRepository expenseRepository) =>
            _expenseRepository = expenseRepository;

        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            _expenseRepository.DeleteAsync(id, cancellationToken);
    }
}
