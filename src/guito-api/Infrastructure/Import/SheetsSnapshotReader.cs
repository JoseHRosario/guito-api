using System.Text.Json;
using System.Text.RegularExpressions;
using Google.Apis.Sheets.v4;
using GuitoApi.Infrastructure.Sheets;
namespace GuitoApi.Infrastructure.Import;

public sealed class SheetsSnapshotReader(IGooglesheetsClientProvider provider)
{
    public async Task<FrozenSheetsSnapshot> CaptureAsync(string spreadsheetId, string tab, int firstRow,
        int lastRow, string currency = "EUR", string decimalSeparator = ".", string dateFormat = "yyyy-MM-dd", CancellationToken cancellationToken = default)
    {
        if (firstRow < 1 || lastRow < firstRow || lastRow - firstRow > 100000 || !Regex.IsMatch(tab, @"^[A-Za-z0-9 _-]+$"))
            throw new ArgumentException("Supply an explicit bounded history range and a simple tab name.");
        var service = await provider.GetAsync(cancellationToken);
        var range = $"{tab}!B{firstRow}:H{lastRow}";
        var count = lastRow - firstRow + 1;
        var formatted = await ReadRowsAsync(service, spreadsheetId, range, count, false, cancellationToken);
        var unformatted = await ReadRowsAsync(service, spreadsheetId, range, count, true, cancellationToken);
        var verifiedFormatted = await ReadRowsAsync(service, spreadsheetId, range, count, false, cancellationToken);
        var verifiedUnformatted = await ReadRowsAsync(service, spreadsheetId, range, count, true, cancellationToken);
        if (!SameRows(formatted, verifiedFormatted) || !SameRows(unformatted, verifiedUnformatted))
            throw new InvalidDataException("Source changed during capture; pause source writes and capture again.");
        return new(spreadsheetId, tab, firstRow, lastRow, currency, decimalSeparator, DateTimeOffset.UtcNow, formatted)
            { UnformattedRows = unformatted, DateFormat = dateFormat };
    }

    private static bool SameRows(List<JsonElement> first, List<JsonElement> second) =>
        first.Count == second.Count && first.Zip(second).All(pair => JsonElement.DeepEquals(pair.First, pair.Second));

    private static async Task<List<JsonElement>> ReadRowsAsync(SheetsService service, string spreadsheetId, string range,
        int count, bool unformatted, CancellationToken cancellationToken)
    {
        var request = service.Spreadsheets.Values.Get(spreadsheetId, range);
        request.ValueRenderOption = unformatted
            ? SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.UNFORMATTEDVALUE
            : SpreadsheetsResource.ValuesResource.GetRequest.ValueRenderOptionEnum.FORMATTEDVALUE;
        using var stream = await request.ExecuteAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var rows = document.RootElement.TryGetProperty("values", out var values)
            ? values.EnumerateArray().Select(r => r.Clone()).ToList() : [];
        if (rows.Count > count) throw new InvalidDataException("Sheets returned rows outside the requested bound.");
        while (rows.Count < count) rows.Add(JsonSerializer.SerializeToElement(Array.Empty<string>()));
        return rows;
    }
}
