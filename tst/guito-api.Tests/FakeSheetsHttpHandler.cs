using System.Text;
using System.Text.RegularExpressions;

namespace GuitoApi.Tests;

/// <summary>
/// Answers the REST calls the Google Sheets client issues, the way the real
/// API would. Records append payloads and delete requests so tests can assert
/// what was written. The service account under test is the dev/staging SA and
/// the spreadsheet id in tests is the (faked) test-sheet.
/// </summary>
public class FakeSheetsHttpHandler : HttpMessageHandler
{
    public const int NextRowIndex = 100; // next row appended
    public const int SmokeSheetId = 201; // numeric tab id of the Smoke Test tab

    /// <summary>Whether the row a DELETE /Smoke read-back targets exists in the fake. Tests toggle per case.</summary>
    public bool SmokeRowExists { get; set; } = true;

    public List<string> AppendBodies { get; } = [];
    public List<string> AppendRanges { get; } = [];
    public List<string> UpdateRanges { get; } = [];
    public List<string> ReadRanges { get; } = [];
    public List<(int SheetId, int StartIndex, int EndIndex)> DeleteDimensions { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pathAndQuery = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        var content = request.Content is null ? "" : await ReadBodyAsync(request.Content, cancellationToken);

        if (pathAndQuery.Contains(":append", StringComparison.OrdinalIgnoreCase))
        {
            if (content.Length > 0) AppendBodies.Add(content);
            // values.append carries the range in the URL path, not a query key: .../values/<range>:append
            var range = ExtractPathSegment(pathAndQuery, "/values/", ":append") ?? "ExpensesAux!B5";
            AppendRanges.Add(range);
            var tab = range.Split('!')[0];
            return Json(200, $$"""
                {
                  "spreadsheetId": "test-sheet",
                  "tableRange": "{{tab}}!B5:H99",
                  "updates": {
                    "spreadsheetId": "test-sheet",
                    "updatedRange": "{{tab}}!B{{NextRowIndex}}:H{{NextRowIndex}}",
                    "updatedRows": 1,
                    "updatedColumns": 7,
                    "updatedCells": 7
                  }
                }
                """);
        }

        if (pathAndQuery.Contains("Config!D2:D24"))
        {
            return Json(200, """
                {
                  "range": "Config!D2:D24",
                  "majorDimension": "ROWS",
                  "values": [["Restaurants"], ["Groceries"], ["Transport"]]
                }
                """);
        }

        if (pathAndQuery.Contains("valueInputOption=", StringComparison.OrdinalIgnoreCase))
        {
            UpdateRanges.Add(pathAndQuery.Split('?')[0]);
            return Json(200, $$"""
                {
                  "spreadsheetId": "test-sheet",
                  "updatedRange": "Expenses!C{{NextRowIndex}}",
                  "updatedRows": 1,
                  "updatedColumns": 2,
                  "updatedCells": 2
                }
                """);
        }

        if (pathAndQuery.Contains(":batchUpdate", StringComparison.OrdinalIgnoreCase))
        {
            // DELETE /Smoke/{id} → deleteDimension on the Smoke Test tab (row ids are 1-based in the
            // API contract, 0-based in the dimension range). Field order in the JSON is not
            // guaranteed, so match each index independently.
            var startIndex = Regex.Match(content, @"""startIndex"":\s*(\d+)");
            var endIndex = Regex.Match(content, @"""endIndex"":\s*(\d+)");
            if (startIndex.Success && endIndex.Success)
                DeleteDimensions.Add((SmokeSheetId, int.Parse(startIndex.Groups[1].Value), int.Parse(endIndex.Groups[1].Value)));
            return Json(200, """{ "spreadsheetId": "test-sheet", "totalUpdatedRows": 0, "totalUpdatedColumns": 0 }""");
        }

        var readRange = ExtractRange(pathAndQuery);

        // DELETE read-back: single-cell Smoke Test row probe.
        if (readRange is not null && Regex.IsMatch(readRange, @"^Smoke Test!B\d+$"))
        {
            ReadRanges.Add(readRange);
            return SmokeRowExists
                ? Json(200, """
                    {
                      "range": "Smoke Test!B5",
                      "majorDimension": "ROWS",
                      "values": [["2026-09-20"]]
                    }
                    """)
                : Json(200, """
                    {
                      "range": "Smoke Test!B5",
                      "majorDimension": "ROWS",
                      "values": []
                    }
                    """);
        }

        // Spreadsheet metadata (no /values/ segment): DELETE resolves the Smoke Test tab id here.
        if (!pathAndQuery.Contains("/values/"))
        {
            return Json(200, $$"""
                {
                  "spreadsheetId": "test-sheet",
                  "properties": { "title": "test-sheet" },
                  "sheets": [
                    { "properties": { "sheetId": {{SmokeSheetId}}, "title": "Smoke Test" } }
                  ]
                }
                """);
        }

        // Default: values.get. Record the requested range, then return canned latest rows.
        if (readRange is not null) ReadRanges.Add(readRange);
        return Json(200, """
                {
                  "range": "Expenses!B90:H99",
                  "majorDimension": "ROWS",
                  "values": [
                    ["2026-09-20", "", "", "12.50", "Coffee", "Restaurants", "user@example.com"],
                    ["2026-09-19", "", "", "45.00", "Groceries", "Groceries", "user@example.com"],
                    ["2026-09-18", "", "", "8.90", "Bus card", "Transport", "user@example.com"]
                  ]
                }
                """);
    }

    private static string? ExtractRange(string pathAndQuery)
    {
        const string marker = "/values/";
        var idx = pathAndQuery.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var rest = pathAndQuery[(idx + marker.Length)..];
        return rest.Split('?')[0];
    }

    private static async Task<string> ReadBodyAsync(HttpContent httpContent, CancellationToken cancellationToken)
    {
        var bytes = await httpContent.ReadAsByteArrayAsync(cancellationToken);
        // The Google client gzips POST bodies (GZipEnabled defaults to true)
        if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            await using var stream = new MemoryStream(bytes);
            await using var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return await reader.ReadToEndAsync();
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string? ExtractPathSegment(string pathAndQuery, string startMarker, string endMarker)
    {
        var start = pathAndQuery.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += startMarker.Length;
        var end = pathAndQuery.IndexOf(endMarker, start, StringComparison.Ordinal);
        if (end < 0) return null;
        return pathAndQuery[start..end];
    }

    private static HttpResponseMessage Json(int statusCode, string body) =>
        new((System.Net.HttpStatusCode)statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
}
