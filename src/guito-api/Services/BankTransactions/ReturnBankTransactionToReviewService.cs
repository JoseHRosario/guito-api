using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankTransactions;

public class ReturnBankTransactionToReviewService(IBankTransactionRepository transactions) : IReturnBankTransactionToReviewService
{
    public async Task<BankTransaction> ReturnBankTransactionToReviewAsync(long id, CancellationToken cancellationToken = default)
    {
        if (id <= 0) throw new ProblemException(400, "Transaction id must be a positive Int64.");
        var transaction = await transactions.ChangeReviewStatusAsync(id, "needsReview", cancellationToken);
        return BankTransactionOutputMapper.ToDto(transaction);
    }
}
