using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.BankTransactions;

public interface IMatchBankTransactionService
{
    Task<BankTransaction> MatchAsync(long id, int expenseId, CancellationToken cancellationToken = default);
}
