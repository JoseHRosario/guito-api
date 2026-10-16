using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankTransactions;

public sealed class UnmatchBankTransactionService(IBankTransactionRepository transactions) : IUnmatchBankTransactionService
{
    public async Task<BankTransaction> UnmatchAsync(long id, int expenseId, CancellationToken cancellationToken = default)
    {
        if (id <= 0 || expenseId <= 0) throw new ProblemException(400, "Positive Transaction and Expense IDs are required.");
        return BankTransactionOutputMapper.ToDto(await transactions.UnmatchAsync(id, expenseId, cancellationToken));
    }
}
