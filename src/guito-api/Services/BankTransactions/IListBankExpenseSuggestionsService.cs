using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Services.BankTransactions;

public interface IListBankExpenseSuggestionsService
{
    Task<IReadOnlyList<BankTransactionLinkedExpense>> ListSuggestionsAsync(long id, CancellationToken cancellationToken = default);
}
