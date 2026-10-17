using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankTransactions;

public sealed class ReadBankCreationReceiptService(IBankExpenseCreationRepository repository) : IReadBankCreationReceiptService
{
    public async Task<BankExpenseCreated> ReadAsync(long id, Guid operationId, CancellationToken cancellationToken)
    {
        if (id <= 0 || operationId == Guid.Empty) throw new ProblemException(400, "Positive Transaction ID and nonempty operationId UUID are required.");
        var receipt = await repository.ReadAsync(id, operationId, cancellationToken)
            ?? throw new ProblemException(404, "No committed creation receipt was found.");
        return new(receipt.OperationId.ToString("D"), receipt.TransactionId, receipt.ExpenseId);
    }
}
