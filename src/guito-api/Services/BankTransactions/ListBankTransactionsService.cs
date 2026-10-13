using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Repositories;
using GuitoApi.Exceptions;

namespace GuitoApi.Services.BankTransactions;

/// <summary>Validates the review filter and maps the repository result to the Bank wire contract.</summary>
public class ListBankTransactionsService(IBankTransactionRepository transactions) : IListBankTransactionsService
{
    public async Task<IReadOnlyList<BankTransaction>> ListAsync(string reviewStatus = "all", CancellationToken cancellationToken = default)
    {
        if (reviewStatus is not ("all" or "needsReview" or "matched" or "ignored"))
            throw new ProblemException(400, "reviewStatus must be all, needsReview, matched or ignored.");
        var stored = await transactions.ListAsync(reviewStatus, cancellationToken);
        return stored.Select(BankTransactionOutputMapper.ToDto).ToList();
    }

}
