using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.BankTransactions;

public interface ICreateAndMatchBankTransactionService
{
    Task<BankExpenseCreated> CreateAsync(long id, BankExpenseCreate request, CancellationToken cancellationToken);
}
