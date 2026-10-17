using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankTransactions;

public sealed class CreateAndMatchBankTransactionService(IBankExpenseCreationRepository repository, IUserIdentityResolver identity)
    : ICreateAndMatchBankTransactionService
{
    public async Task<BankExpenseCreated> CreateAsync(long id, BankExpenseCreate request, CancellationToken cancellationToken)
    {
        if (id <= 0 || request.OperationId == Guid.Empty) throw new ProblemException(400, "Positive Transaction ID and nonempty operationId UUID are required.");
        var draft = new ExpenseDraft
        {
            BookingDate = TimeService.InLisbon(request.BookingDate), Amount = request.Amount,
            Currency = request.Currency, Description = request.Description, Category = request.Category,
            CreatorEmail = identity.GetEmail()
        };
        var receipt = await repository.CreateAsync(id, request.OperationId, draft, cancellationToken);
        return new(receipt.OperationId.ToString("D"), receipt.TransactionId, receipt.ExpenseId);
    }
}
