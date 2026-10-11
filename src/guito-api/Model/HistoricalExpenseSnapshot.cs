namespace GuitoApi.Model;

public sealed record HistoricalExpenseSnapshot(string SnapshotId, string SpreadsheetId, string Tab,
    int FirstRow, int LastRow, string Currency, string DecimalSeparator, DateTimeOffset CapturedAt,
    IReadOnlyList<HistoricalImportRow> Rows)
{
    public string DateFormat { get; init; } = "yyyy-MM-dd";
}
