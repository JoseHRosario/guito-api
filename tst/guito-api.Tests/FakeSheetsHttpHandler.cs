namespace GuitoApi.Tests;

/// <summary>
/// Answers the REST calls the Google Sheets client issues, the way the real
/// API would. Records append payloads so tests can assert what was written.
/// </summary>
public class FakeSheetsHttpHandler : HttpMessageHandler
{
    public const int NextRowIndex = 100; // next row appended to ExpensesAux

    public List<string> AppendBodies { get; } = [];
    public List<string> AppendRanges { get; } = [];
    public List<string> UpdateRanges { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pathAndQuery = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        var content = request.Content is null ? "" : await ReadBodyAsync(request.Content, cancellationToken);

        if (pathAndQuery.Contains(":append", StringComparison.OrdinalIgnoreCase))
        {
            if (content.Length > 0) AppendBodies.Add(content);
            var range = ExtractQueryValue(pathAndQuery, "range") ?? "ExpensesAux!B5";
            AppendRanges.Add(range);
            return Json(200, $$"""
                {
                  "spreadsheetId": "test-sheet",
                  "tableRange": "ExpensesAux!B5:H99",
                  "updates": {
                    "spreadsheetId": "test-sheet",
                    "updatedRange": "ExpensesAux!B{{NextRowIndex}}:H{{NextRowIndex}}",
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
                  "updatedRange": "ExpensesAux!C{{NextRowIndex}}",
                  "updatedRows": 1,
                  "updatedColumns": 2,
                  "updatedCells": 2
                }
                """);
        }

        // Default: values.get over the latest-expenses range (ExpensesAux!B{first}:H{last})
        return Json(200, """
                {
                  "range": "ExpensesAux!B90:H99",
                  "majorDimension": "ROWS",
                  "values": [
                    ["2026-09-20", "", "", "12.50", "Coffee", "Restaurants", "user@example.com"],
                    ["2026-09-19", "", "", "45.00", "Groceries", "Groceries", "user@example.com"],
                    ["2026-09-18", "", "", "8.90", "Bus card", "Transport", "user@example.com"]
                  ]
                }
                """);
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
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static string? ExtractQueryValue(string pathAndQuery, string key)
    {
        var query = pathAndQuery.Split('?').Skip(1).FirstOrDefault();
        if (query is null) return null;
        foreach (var pair in query.Split('&'))
        {
            var kv = pair.Split('=');
            if (kv.Length == 2 && kv[0] == key) return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    private static HttpResponseMessage Json(int statusCode, string body) =>
        new((System.Net.HttpStatusCode)statusCode)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
}