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
        if (value.Date == default)
            throw new GuitoApi.Exceptions.ProblemException(400, "date is required.");
        var draft = new ExpenseDraft
        {
            Date = value.Date.Date, OccurredAt = ExpenseTime.Resolve(value.Date, value.OccurredAt),
            Amount = value.Amount, Currency = value.Currency,
            Description = value.Description, Category = value.Category, CreatorEmail = identity.GetEmail()
        };
        return new ExpenseCreated { Id = await repository.CreateAsync(draft, cancellationToken) };
    }
}
