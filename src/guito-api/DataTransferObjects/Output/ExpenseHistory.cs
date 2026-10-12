namespace GuitoApi.DataTransferObjects.Output;

public sealed record ExpenseHistory(int PageSize, int PageIndex, long TotalCount,
    bool HasMore, IReadOnlyList<ExpenseHistoryDetail> Expenses);
