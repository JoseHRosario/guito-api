using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.BankTransactions;

public interface IReadBankCreationReceiptService
{
    Task<BankExpenseCreated> ReadAsync(long id, Guid operationId, CancellationToken cancellationToken);
}
