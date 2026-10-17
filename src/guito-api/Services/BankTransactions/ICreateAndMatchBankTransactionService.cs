using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.BankTransactions;

public interface ICreateAndMatchBankTransactionService
{
    Task<BankTransaction> CreateAsync(long id, ExpenseCreate request, CancellationToken cancellationToken);
}
