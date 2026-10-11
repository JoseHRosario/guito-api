namespace GuitoApi.Model;

public sealed record ExpenseHistoryPage(long TotalCount, IReadOnlyList<ExpenseHistoryEntry> Expenses);
