using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Repositories;
using System.Globalization;

namespace GuitoApi.Services.Expense
{
    /// <summary>
    /// Adapter over <see cref="IExpenseRepository"/> (repository-pattern refactor,
    /// ADR 0011): keeps the ICreateExpenseService contract so controllers and the
    /// wire shape are unchanged, while every Sheets behavior moved into the
    /// repository layer. The repository's opaque string Id is the sheet row index
    /// today; the wire still carries it as the numeric Id of ExpenseCreated.
    /// </summary>
    public class CreateExpenseService : ICreateExpenseService
    {
        private readonly IExpenseRepository _expenseRepository;

        public CreateExpenseService(IExpenseRepository expenseRepository) =>
            _expenseRepository = expenseRepository;

        public async Task<ExpenseCreated> CreateAsync(ExpenseCreate value, CancellationToken cancellationToken = default)
        {
            var id = await _expenseRepository.CreateAsync(value, cancellationToken);
            return new ExpenseCreated { Id = int.Parse(id, CultureInfo.InvariantCulture) };
        }
    }
}
