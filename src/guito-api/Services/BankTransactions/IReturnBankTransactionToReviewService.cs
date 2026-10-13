using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.BankTransactions;

public interface IReturnBankTransactionToReviewService
{
    Task<BankTransaction> ReturnBankTransactionToReviewAsync(long id, CancellationToken cancellationToken = default);
}
