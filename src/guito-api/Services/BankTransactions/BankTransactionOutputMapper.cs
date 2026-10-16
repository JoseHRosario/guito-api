using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Model;

namespace GuitoApi.Services.BankTransactions;

public static class BankTransactionOutputMapper
{
    public static BankTransaction ToDto(BankTransactionDetail transaction) => new(
        Id: transaction.Id,
        AccountUid: transaction.AccountUid,
        BookingDate: transaction.BookingDate,
        Amount: transaction.Amount,
        Currency: transaction.Currency,
        RemittanceInformation: transaction.RemittanceInformation,
        SuggestedCategory: transaction.SuggestedCategory is { } suggestion
            ? new SuggestedCategory(suggestion.Id, suggestion.Name)
            : null,
        Status: transaction.Status,
        ReviewStatus: transaction.ReviewStatus,
        LinkedExpense: transaction.LinkedExpense is { } expense
            ? new BankTransactionLinkedExpense(expense.Id, expense.BookingDate, expense.Amount,
                expense.Currency, expense.Description, expense.Category) : null);
}
