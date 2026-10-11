namespace GuitoApi.Model;

public sealed class HistoricalImportReport
{
    public string SnapshotId { get; init; } = "";
    public List<HistoricalImportRow> Rows { get; init; } = [];
    public List<string> Exceptions { get; } = [];
    public int RetainedPreviousCount { get; set; }
    public int ImportedCount => Rows.Count(r => r.Status == "Imported" && r.Parity);
    public int BlankCount => Rows.Count(r => r.Status == "Blank");
    public int NonExpenseCount => Rows.Count(r => r.Status == "NonExpense");
    public int MalformedCount => Rows.Count(r => r.Status == "Malformed");
    public int FailedCount => Rows.Count(r => r.Status == "Failed");
    public int ParityMismatchCount => Rows.Count(r => r.Status == "Imported" && !r.Parity);
    public int ExcludedCount => Rows.Count(r => r.Status == "Excluded");
    public string SourceTotal { get; set; } = "0";
    public string PersistedTotal { get; set; } = "0";
    public string ExcludedSourceTotal { get; set; } = "0";
    public string ImportedTotal { get; set; } = "0";
    public bool ReadyForCutover => Exceptions.Count == 0 && Rows.All(r =>
        r.Status is "Blank" or "NonExpense" or "Excluded" || r.Status == "Imported" && r.Parity);
}
