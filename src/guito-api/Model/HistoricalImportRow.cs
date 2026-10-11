namespace GuitoApi.Model;

public sealed class HistoricalImportRow
{
    public int SourceRow { get; init; }
    public string RawJson { get; init; } = "";
    public string Status { get; set; } = "Pending";
    public string? Exception { get; set; }
    public ExpenseDraft? Expected { get; init; }
    public ImportExclusion? Exclusion { get; set; }
    public int? ExpenseId { get; set; }
    public string? Currency { get; set; }
    public string? Amount { get; set; }
    public string? BookingDate { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? CreatorEmail { get; set; }
    public string? CreationDate { get; set; }
    public string? UpdateDate { get; set; }
    public bool Parity { get; set; }
}
