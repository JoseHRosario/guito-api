using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GuitoApi.Infrastructure.Serialization;
using GuitoApi.Model;
using GuitoApi.Services;
namespace GuitoApi.Infrastructure.Import;

public static class HistoricalRowParser
{
    private static readonly JsonSerializerOptions DecimalOptions = new() { Converters = { new ExactDecimalJsonConverter() } };

    public static HistoricalImportRow Parse(FrozenSheetsSnapshot snapshot, int offset)
    {
        var raw = snapshot.Rows[offset];
        var unformatted = snapshot.UnformattedRows?[offset] ?? raw;
        var row = new HistoricalImportRow { SourceRow = snapshot.FirstRow + offset,
            RawJson = JsonSerializer.Serialize(new { Formatted = raw, Unformatted = unformatted }) };
        try { return ParseExpense(snapshot, raw, row); }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or GuitoApi.Exceptions.ProblemException)
        { row.Status = "Malformed"; row.Exception = exception.Message; return row; }
    }

    private static HistoricalImportRow ParseExpense(FrozenSheetsSnapshot snapshot, JsonElement raw, HistoricalImportRow row)
    {
        if (raw.ValueKind != JsonValueKind.Array || raw.GetArrayLength() > 7) throw new FormatException("Expected frozen B:H row layout.");
        var cells = raw.EnumerateArray().Select(CellText).ToArray();
        var effective = snapshot.UnformattedRows?[row.SourceRow - snapshot.FirstRow] ?? raw;
        ValidateVisibility(cells, effective);
        if (cells.All(string.IsNullOrWhiteSpace)) { row.Status = "Blank"; return row; }
        // Only formula-only Year/Month rows are non-expenses. Any business cell is relevant.
        if (new[] { 0, 3, 4, 5, 6 }.All(i => string.IsNullOrWhiteSpace(Cell(cells, i))))
        { row.Status = "NonExpense"; return row; }
        return ParseRelevantExpense(snapshot, row, cells, effective);
    }

    private static HistoricalImportRow ParseRelevantExpense(FrozenSheetsSnapshot snapshot, HistoricalImportRow row, string[] cells, JsonElement effective)
    {
        if (snapshot.Currency != "EUR") throw new FormatException("Source convention must explicitly be EUR.");
        if (snapshot.DateFormat is not ("yyyy-MM-dd" or "dd/MM/yyyy" or "d/M/yyyy" or "MM/dd/yyyy"))
            throw new FormatException("Unsupported explicit source date convention.");
        var date = DateTime.ParseExact(Cell(cells, 0), snapshot.DateFormat, CultureInfo.InvariantCulture);
        var displayAmount = ParseAmount(Cell(cells, 3), snapshot.DecimalSeparator);
        ValidateAttribution(effective, cells, date);
        var amount = ReadUnderlyingAmount(effective, displayAmount, snapshot.DecimalSeparator);
        var category = Cell(cells, 5);
        var creator = Cell(cells, 6);
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(creator)) throw new FormatException("Category and creator attribution are required.");
        return new HistoricalImportRow { SourceRow = row.SourceRow, RawJson = row.RawJson, Expected = new ExpenseDraft
        { BookingDate = TimeService.Midnight(date), Amount = amount, Description = Cell(cells, 4), Category = category, CreatorEmail = creator } };
    }

    private static void ValidateVisibility(string[] cells, JsonElement effective)
    {
        if (effective.ValueKind != JsonValueKind.Array || effective.GetArrayLength() > 7) throw new FormatException("Underlying row has invalid layout.");
        foreach (var index in new[] { 0, 3, 4, 5, 6 })
            if (string.IsNullOrWhiteSpace(Cell(cells, index)) && effective.GetArrayLength() > index && !string.IsNullOrWhiteSpace(CellText(effective[index])))
                throw new FormatException("Hidden underlying business cell must not be skipped as blank/non-expense.");
    }

    private static void ValidateAttribution(JsonElement effective, string[] cells, DateTime date)
    {
        if (effective.GetArrayLength() <= 6) throw new FormatException("Underlying attribution is incomplete.");
        foreach (var index in new[] { 4, 5, 6 })
            if (CellText(effective[index]) != Cell(cells, index)) throw new FormatException("Attribution changed during capture.");
        if (effective[0].ValueKind != JsonValueKind.Number)
        {
            if (CellText(effective[0]) != Cell(cells, 0)) throw new FormatException("Original day changed during capture.");
            return;
        }
        var serial = JsonSerializer.Deserialize<decimal>(effective[0].GetRawText(), DecimalOptions);
        if (serial != (decimal)(date.Date - new DateTime(1899, 12, 30)).TotalDays)
            throw new FormatException("Underlying date disagrees with displayed original day or carries unsupported historical time.");
    }

    private static decimal ReadUnderlyingAmount(JsonElement effective, decimal displayAmount, string separator)
    {
        if (effective.ValueKind != JsonValueKind.Array || effective.GetArrayLength() <= 3)
            throw new FormatException("Underlying amount is missing; frozen source changed during capture.");
        var amount = effective[3];
        if (amount.ValueKind == JsonValueKind.Number)
            return JsonSerializer.Deserialize<decimal>(amount.GetRawText(), DecimalOptions);
        var parsed = ParseAmount(CellText(amount), separator);
        if (parsed != displayAmount) throw new FormatException("Text amount changed between formatted and underlying capture.");
        return parsed;
    }

    private static decimal ParseAmount(string text, string separator)
    {
        if (separator is not ("." or ",")) throw new FormatException("Decimal separator must be explicit '.' or ','.");
        var match = Regex.Match(text, @"^\s*(?:EUR\s+|€\s*)?(?<amount>[+-]?[0-9]+(?:[.,][0-9]+)?(?:[eE][+-]?[0-9]+)?)(?:\s*€|\s+EUR)?\s*$");
        if (!match.Success) throw new FormatException("Amount has unsupported or conflicting currency/format.");
        var number = match.Groups["amount"].Value;
        if (number.Contains(separator == "." ? ',' : '.')) throw new FormatException("Amount conflicts with the declared decimal separator; grouping is unsupported.");
        return JsonSerializer.Deserialize<decimal>(JsonSerializer.Serialize(number.Replace(',', '.')), DecimalOptions);
    }

    private static string Cell(string[] cells, int index) => index < cells.Length ? cells[index] : "";
    private static string CellText(JsonElement cell) => cell.ValueKind switch
    { JsonValueKind.String => cell.GetString()!, JsonValueKind.Number => cell.GetRawText(), JsonValueKind.Null => "", _ => throw new FormatException("Unsupported Sheets cell type.") };
}
