using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Expense
{
    /// <summary>
    /// Adapter over <see cref="IExpenseRepository"/> (repository-pattern refactor,
    /// ADR 0011): keeps the IListLatestExpensesService contract so controllers and
    /// the wire shape are unchanged, while every Sheets behavior moved into the
    /// repository layer.
    /// </summary>
    public class ListLatestExpensesService : IListLatestExpensesService
    {
        private readonly IExpenseRepository _expenseRepository;

        public ListLatestExpensesService(IExpenseRepository expenseRepository) =>
            _expenseRepository = expenseRepository;

        public async Task<ExpenseListLatest> ListLatestAsync(int count, CancellationToken cancellationToken = default)
        {
            var expenses = await _expenseRepository.ListLatestAsync(count, cancellationToken);
            return new ExpenseListLatest { Expenses = [.. expenses] };
        }
    }
}
