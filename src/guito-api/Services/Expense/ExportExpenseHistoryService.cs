using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Repositories;

namespace GuitoApi.Services.Expense;

public sealed class ExportExpenseHistoryService(IExpenseHistoryRepository repository) : IExportExpenseHistoryService
{
    public async Task<ExpenseHistory> ReadAsync(int pageSize, string? cursor, CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100)
            throw new ProblemException(400, "History pageSize must be between 1 and 100.");
        var page = await repository.ReadAsync(pageSize, cursor, cancellationToken);
        return new(page.SnapshotId, page.ExpiresAt, page.TotalCount,
            page.Expenses.Select(e => new ExpenseHistoryDetail(e.Id, e.BookingDate, e.CreationDate,
                e.UpdateDate, e.Amount, e.Currency, e.Description, e.Category, e.CreatorEmail)).ToList(), page.NextCursor);
    }
}
