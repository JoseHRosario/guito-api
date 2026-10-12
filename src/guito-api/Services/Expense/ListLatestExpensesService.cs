using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Expense;

public class ListLatestExpensesService(IExpenseRepository repository) : IListLatestExpensesService
{
    public async Task<ExpenseListLatest> ListLatestAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > 100)
            throw new GuitoApi.Exceptions.ProblemException(400, "Latest count must be between 1 and 100.");
        var expenses = await repository.ListLatestAsync(count, cancellationToken);
        return new ExpenseListLatest { Expenses = expenses.Select(ExpenseOutputMapper.ToOutput).ToList() };
    }
}
