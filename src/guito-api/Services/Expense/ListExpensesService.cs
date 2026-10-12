using System.Globalization;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Expense;

public class ListExpensesService(IExpenseRepository repository) : IListExpensesService
{
    public async Task<ExpenseListLatest> ListAsync(string? category, string? month, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(category))
            throw new GuitoApi.Exceptions.ProblemException(400, "category is required and must not be blank.");
        var start = ParseMonth(month);
        var expenses = await repository.ListAsync(category, TimeService.CalendarMonthStart(start),
            start == new DateTime(9999, 12, 1) ? null : TimeService.CalendarMonthStart(start.AddMonths(1)), cancellationToken);
        return new ExpenseListLatest { Expenses = expenses.Select(ExpenseOutputMapper.ToOutput).ToList() };
    }

    private static DateTime ParseMonth(string? month)
    {
        if (month is not { Length: 7 } || month[4] != '-'
            || month.Where((_, index) => index != 4).Any(c => c is < '0' or > '9')
            || !DateTime.TryParseExact($"{month}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start))
            throw new GuitoApi.Exceptions.ProblemException(400, "month is required and must be a valid YYYY-MM calendar month.");
        return start;
    }
}
