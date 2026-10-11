using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace GuitoApi.Infrastructure.Import;

public static class SnapshotArchive
{
    public static string Identity(FrozenSheetsSnapshot snapshot) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { snapshot.SpreadsheetId, snapshot.Tab, snapshot.FirstRow,
            snapshot.LastRow, snapshot.Currency, snapshot.DecimalSeparator, snapshot.DateFormat, snapshot.Rows, snapshot.UnformattedRows }))));

    public static async Task<string> SaveAsync(string directory, FrozenSheetsSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Identity(snapshot)}.snapshot.json");
        if (File.Exists(path))
        {
            var existing = await LoadAsync(path, cancellationToken);
            if (Identity(existing) != Identity(snapshot)) throw new InvalidDataException("Archive identity mismatch.");
            return path;
        }
        var temporary = $"{path}.{Guid.NewGuid():N}.partial";
        try
        {
            await WriteArchiveAsync(temporary, snapshot, cancellationToken);
            File.Move(temporary, path, overwrite: false);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task WriteArchiveAsync(string path, FrozenSheetsSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, snapshot, cancellationToken: cancellationToken);
        await file.FlushAsync(cancellationToken);
    }

    public static async Task<FrozenSheetsSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var file = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<FrozenSheetsSnapshot>(file, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Snapshot is missing.");
    }
}
