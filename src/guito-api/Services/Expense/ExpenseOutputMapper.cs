using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Model;

namespace GuitoApi.Services.Expense;

internal static class ExpenseOutputMapper
{
    internal static ExpenseListLatestDetail ToOutput(ExpenseSummary expense) => new()
    {
        Id = expense.Id, BookingDate = expense.BookingDate,
        CreationDate = expense.CreationDate, UpdateDate = expense.UpdateDate, Currency = expense.Currency,
        Amount = expense.Amount, Description = expense.Description, Category = expense.Category,
        CreatorEmail = expense.CreatorEmail
    };
}
