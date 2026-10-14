using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Expense;

public sealed class ReadExpenseDetailService(IExpenseRepository repository) : IReadExpenseDetailService
{
    public async Task<ExpenseDetailResponse> ReadAsync(int id, CancellationToken cancellationToken = default)
    {
        var detail = await repository.ReadAsync(id, cancellationToken);
        return ExpenseDetailResponseMapper.Map(detail);
    }
}
