namespace GuitoApi.Model;

public sealed record ExpenseHistoryPage(Guid SnapshotId, DateTimeOffset ExpiresAt, long TotalCount,
    IReadOnlyList<ExpenseHistoryEntry> Expenses, string? NextCursor);
