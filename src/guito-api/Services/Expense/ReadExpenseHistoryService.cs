using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Expense;

public sealed class ReadExpenseHistoryService(IExpenseHistoryRepository repository) : IReadExpenseHistoryService
{
    public async Task<ExpenseHistory> ReadAsync(int pageSize, int pageIndex, CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100 || pageIndex < 0)
            throw new ProblemException(400, "pageSize must be between 1 and 100; pageIndex must be nonnegative.");
        var offset = (long)pageSize * pageIndex;
        var page = await repository.ReadAsync(pageSize, offset, cancellationToken);
        return new(pageSize, pageIndex, page.TotalCount, offset + page.Expenses.Count < page.TotalCount,
            page.Expenses.Select(ToOutput).ToList());
    }

    private static ExpenseHistoryDetail ToOutput(ExpenseHistoryEntry expense) => new(expense.Id,
        expense.Amount, "EUR", expense.BookingDate, expense.Description, expense.Category,
        expense.CreatorEmail, expense.CreationDate, expense.UpdateDate);
}
