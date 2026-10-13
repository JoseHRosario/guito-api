using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankTransactions;

public class IgnoreBankTransactionService(IBankTransactionRepository transactions) : IIgnoreBankTransactionService
{
    public async Task<BankTransaction> IgnoreBankTransactionAsync(long id, CancellationToken cancellationToken = default)
    {
        if (id <= 0) throw new ProblemException(400, "Transaction id must be a positive Int64.");
        var transaction = await transactions.ChangeReviewStatusAsync(id, "ignored", cancellationToken);
        return BankTransactionOutputMapper.ToDto(transaction);
    }
}
