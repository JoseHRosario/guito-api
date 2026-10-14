using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Expense;

public sealed class ReadExpenseDetailService(IExpenseDetailRepository repository) : IReadExpenseDetailService
{
    public async Task<ExpenseDetailResponse> ReadAsync(int id, CancellationToken cancellationToken = default)
    {
        var detail = await repository.ReadAsync(id, cancellationToken);
        return new(detail.Id, detail.Amount, detail.Description, detail.Category, detail.BookingDate,
            detail.CreatorEmail, detail.CreationDate, detail.UpdateDate, detail.Revision);
    }
}
