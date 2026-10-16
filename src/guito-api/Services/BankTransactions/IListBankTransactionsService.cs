using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.BankTransactions;

/// <summary>Lists the Bank review inbox, optionally filtered by persisted review status.</summary>
public interface IListBankTransactionsService
{
    Task<IReadOnlyList<BankTransaction>> ListAsync(string reviewStatus = "all", CancellationToken cancellationToken = default);
}
