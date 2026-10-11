using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Model;
using GuitoApi.Repositories;
using GuitoApi.Services;

namespace GuitoApi.Services.Expense;

public class CreateExpenseService(IExpenseRepository repository, IUserIdentityResolver identity) : ICreateExpenseService
{
    public async Task<ExpenseCreated> CreateAsync(ExpenseCreate value, CancellationToken cancellationToken = default)
    {
        var bookingDate = TimeService.InLisbon(value.BookingDate);
        var draft = new ExpenseDraft
        {
            BookingDate = bookingDate,
            Amount = value.Amount, Currency = value.Currency,
            Description = value.Description, Category = value.Category, CreatorEmail = identity.GetEmail()
        };
        return new ExpenseCreated { Id = await repository.CreateAsync(draft, cancellationToken) };
    }
}
