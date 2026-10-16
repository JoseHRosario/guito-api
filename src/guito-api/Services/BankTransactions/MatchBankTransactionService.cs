using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankTransactions;

public sealed class MatchBankTransactionService(IBankTransactionRepository transactions) : IMatchBankTransactionService
{
    public async Task<BankTransaction> MatchAsync(long id, int expenseId, CancellationToken cancellationToken = default)
    {
        if (id <= 0 || expenseId <= 0) throw new ProblemException(400, "Positive Transaction and Expense IDs are required.");
        return BankTransactionOutputMapper.ToDto(await transactions.MatchAsync(id, expenseId, cancellationToken));
    }
}
