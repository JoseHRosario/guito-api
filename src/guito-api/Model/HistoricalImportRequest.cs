namespace GuitoApi.Model;

public sealed class HistoricalImportRequest
{
    public required HistoricalExpenseSnapshot Snapshot { get; init; }
    public ImportReconciliation? Reconciliation { get; init; }
    public Dictionary<int, ImportExclusion> Exclusions { get; init; } = [];
}
