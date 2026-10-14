using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Expense;

public sealed class UpdateExpenseService(IExpenseRepository repository, IExpenseRevisionResolver revision) : IUpdateExpenseService
{
    public async Task<ExpenseDetailResponse> UpdateAsync(int id, ExpenseUpdate value, CancellationToken cancellationToken = default)
    {
        var edit = new ExpenseEdit(value.Description, value.Category, value.Amount, TimeService.InLisbon(value.BookingDate));
        var detail = await repository.UpdateAsync(id, revision.RequireRevision(), edit, cancellationToken);
        return ExpenseDetailResponseMapper.Map(detail);
    }
}
