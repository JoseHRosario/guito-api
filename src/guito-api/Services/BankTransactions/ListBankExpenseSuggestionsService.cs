using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankTransactions;

public sealed class ListBankExpenseSuggestionsService(IBankTransactionRepository transactions) : IListBankExpenseSuggestionsService
{
    public async Task<IReadOnlyList<BankTransactionLinkedExpense>> ListSuggestionsAsync(long id, CancellationToken cancellationToken = default)
    {
        if (id <= 0) throw new ProblemException(400, "Transaction id must be a positive Int64.");
        var suggestions = await transactions.ListSuggestionsAsync(id, cancellationToken);
        return suggestions.Select(expense => new BankTransactionLinkedExpense(expense.Id, expense.BookingDate,
            expense.Amount, expense.Currency, expense.Description, expense.Category)).ToList();
    }
}
