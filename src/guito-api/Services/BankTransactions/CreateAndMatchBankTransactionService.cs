using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankTransactions;

public sealed class CreateAndMatchBankTransactionService(IBankExpenseCreationRepository repository, IUserIdentityResolver identity)
    : ICreateAndMatchBankTransactionService
{
    public async Task<BankTransaction> CreateAsync(long id, ExpenseCreate request, CancellationToken cancellationToken)
    {
        if (id <= 0) throw new ProblemException(400, "Positive Transaction ID is required.");
        var draft = new ExpenseDraft
        {
            BookingDate = TimeService.InLisbon(request.BookingDate), Amount = request.Amount,
            Currency = request.Currency, Description = request.Description, Category = request.Category,
            CreatorEmail = identity.GetEmail()
        };
        return BankTransactionOutputMapper.ToDto(await repository.CreateAsync(id, draft, cancellationToken));
    }
}
