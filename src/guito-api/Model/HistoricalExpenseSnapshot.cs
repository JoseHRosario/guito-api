using System.Text.Json;
namespace GuitoApi.Model;

public sealed record HistoricalExpenseSnapshot(string SpreadsheetId, string Tab, int FirstRow, int LastRow,
    string Currency, string DecimalSeparator, DateTimeOffset CapturedAt, IReadOnlyList<JsonElement> Rows)
{
    public string DateFormat { get; init; } = "yyyy-MM-dd";
    public IReadOnlyList<JsonElement>? UnformattedRows { get; init; }
}
