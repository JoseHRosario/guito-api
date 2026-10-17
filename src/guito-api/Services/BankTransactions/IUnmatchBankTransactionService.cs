using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.BankTransactions;

public interface IUnmatchBankTransactionService
{
    Task<BankTransaction> UnmatchAsync(long id, int expenseId, CancellationToken cancellationToken = default);
}
