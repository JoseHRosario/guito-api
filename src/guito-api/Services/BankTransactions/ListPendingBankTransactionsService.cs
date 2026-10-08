using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Services.BankTransactions;

/// <summary>
/// Thin adapter: the pending list and its ordering live entirely in the
/// repository (issue #88); this service only crosses the Model→wire-DTO
/// boundary for the controller (issue #91).
/// </summary>
public class ListPendingBankTransactionsService(IBankTransactionRepository transactions) : IListPendingBankTransactionsService
{
    public async Task<IReadOnlyList<BankTransactionPending>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await transactions.ListPendingAsync(cancellationToken);
        return pending.Select(ToDto).ToList();
    }

    private static BankTransactionPending ToDto(BankTransactionPendingDetail transaction) => new(
        Id: transaction.Id,
        AccountUid: transaction.AccountUid,
        BookingDate: transaction.BookingDate,
        Amount: transaction.Amount,
        Currency: transaction.Currency,
        RemittanceInformation: transaction.RemittanceInformation,
        SuggestedCategory: transaction.SuggestedCategory is { } suggestion
            ? new SuggestedCategory(suggestion.Id, suggestion.Name)
            : null);
}
