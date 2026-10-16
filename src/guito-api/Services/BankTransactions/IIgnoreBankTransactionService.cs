using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.BankTransactions;

public interface IIgnoreBankTransactionService
{
    Task<BankTransaction> IgnoreBankTransactionAsync(long id, CancellationToken cancellationToken = default);
}
