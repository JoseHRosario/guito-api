namespace GuitoApi.DataTransferObjects.Output;

public sealed record ExpenseHistory(Guid SnapshotId, DateTimeOffset ExpiresAt, long TotalCount,
    IReadOnlyList<ExpenseHistoryDetail> Expenses, string? NextCursor);
