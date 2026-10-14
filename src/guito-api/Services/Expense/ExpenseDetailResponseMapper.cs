using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Model;

namespace GuitoApi.Services.Expense;

internal static class ExpenseDetailResponseMapper
{
    public static ExpenseDetailResponse Map(ExpenseDetail detail) =>
        new(detail.Id, detail.Amount, detail.Description, detail.Category, detail.BookingDate,
            detail.CreatorEmail, detail.CreationDate, detail.UpdateDate, detail.Revision);
}
