namespace GuitoApi.Model;

public sealed class ImportReconciliation
{
    public required string PreviousSnapshotId { get; init; }
    public required string ApprovedBy { get; init; }
    public required string Reason { get; init; }
    public Dictionary<int, int> ReuseRows { get; init; } = [];
    public List<int> NewRows { get; init; } = [];
    public List<int> RetainPreviousRows { get; init; } = [];
}
