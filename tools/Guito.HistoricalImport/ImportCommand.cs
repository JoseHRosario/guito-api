using System.Globalization;
using System.Text.Json;
using Amazon;
using Amazon.RDSDataService;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using GuitoApi.Configuration;
using GuitoApi.Infrastructure.Import;
using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Infrastructure.Serialization;
using GuitoApi.Model;
using GuitoApi.Services.Import;
using Microsoft.Extensions.Options;
namespace Guito.HistoricalImport;

internal static class ImportCommand
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true, Converters = { new ExactDecimalJsonConverter() } };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] == "--help")
        {
            Console.WriteLine("capture <repo-root> <last-row> <archive-dir> <decimal-separator> | validate <snapshot-file> <report-file> | import <repo-root> <snapshot-file> <report-file> [approval-file]");
            Console.WriteLine("Capture date convention: GUITO_SOURCE_DATE_FORMAT=yyyy-MM-dd (default), dd/MM/yyyy, d/M/yyyy, MM/dd/yyyy or M/d/yyyy. EUR only; dot decimals allow strict comma thousands groups. Displayed and underlying amounts must agree exactly.");
            return 0;
        }
        return args[0] switch
        {
            "capture" when args.Length == 5 => await CaptureAsync(args),
            "validate" when args.Length == 3 => await ValidateAsync(args),
            "import" when args.Length is 4 or 5 => await ImportAsync(args),
            _ => throw new ArgumentException("Unknown command; use --help.")
        };
    }

    private static async Task<int> CaptureAsync(string[] args)
    {
        var source = ReadSource(args[1]);
        var reader = new SheetsSnapshotReader(new ImportSheetsClientProvider(RequiredEnvironment("GOOGLE_KEY_FILE")));
        var snapshot = await reader.CaptureAsync(source.SpreadsheetId, source.Tab, source.FirstRow, int.Parse(args[2], CultureInfo.InvariantCulture), decimalSeparator: args[4], dateFormat: Environment.GetEnvironmentVariable("GUITO_SOURCE_DATE_FORMAT") ?? "yyyy-MM-dd");
        var path = await SnapshotArchive.SaveAsync(Path.GetFullPath(args[3]), snapshot);
        Console.WriteLine($"Captured {snapshot.Rows.Count} bounded rows. Snapshot {SnapshotArchive.Identity(snapshot)}. Archive: {path}");
        return 0;
    }

    private static async Task<int> ValidateAsync(string[] args)
    {
        var snapshot = await LoadVerifiedAsync(args[1]);
        var rows = snapshot.ToDomain().Rows;
        await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(rows, ReportOptions));
        Console.WriteLine($"Validated {rows.Count} bounded rows; {rows.Count(r => r.Status == "Malformed")} malformed. No database access.");
        return rows.Any(r => r.Status == "Malformed") ? 2 : 0;
    }

    private static async Task<int> ImportAsync(string[] args)
    {
        if (RequiredEnvironment("GITHUB_ACTIONS") != "true" || RequiredEnvironment("GUITO_IMPORT_CONFIRM") != "staging")
            throw new InvalidOperationException("Live imports are staging-only GitHub Actions operations.");
        var snapshot = await LoadVerifiedAsync(args[2]);
        var source = ReadSource(args[1]);
        if (snapshot.SpreadsheetId != source.SpreadsheetId || snapshot.Tab != source.Tab || snapshot.FirstRow != source.FirstRow)
            throw new InvalidOperationException("Snapshot source does not match configured staging history.");
        await SnapshotArchive.SaveAsync(Path.GetDirectoryName(Path.GetFullPath(args[3]))!, snapshot);
        return await WriteImportReportAsync(args, snapshot);
    }

    private static async Task<int> WriteImportReportAsync(string[] args, FrozenSheetsSnapshot snapshot)
    {
        var request = await ReadRequestAsync(args, snapshot);
        using var tokenClient = new AmazonSecurityTokenServiceClient(RegionEndpoint.EUWest1);
        var identity = await tokenClient.GetCallerIdentityAsync(new GetCallerIdentityRequest());
        if (!identity.Arn.StartsWith("arn:aws:sts::497087877832:assumed-role/guito-api-deploy/", StringComparison.Ordinal))
            throw new InvalidOperationException("Rehearsal requires the existing pipeline deployment role.");
        var options = StagingDatabase();
        using var sdk = new AmazonRDSDataServiceClient(RegionEndpoint.EUWest1);
        var context = new PostgresTransactionContext();
        var client = new DataApiClient(sdk, Options.Create(options), context);
        await using var unit = new DataApiUnitOfWork(client, context);
        var report = await new HistoricalExpenseImportService(new ArchivedSheetsSnapshotRepository(), new DataApiHistoricalImportRepository(client, unit)).ImportAsync(request);
        await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(report, ReportOptions));
        Console.WriteLine($"Snapshot {report.SnapshotId}: imported={report.ImportedCount}, excluded={report.ExcludedCount}, ready={report.ReadyForCutover}. Protected report written.");
        return report.ReadyForCutover ? 0 : 2;
    }

    private static async Task<HistoricalImportRequest> ReadRequestAsync(string[] args, FrozenSheetsSnapshot snapshot)
    {
        if (args.Length == 4) return new() { Snapshot = snapshot.ToDomain() };
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(args[4]));
        if (document.RootElement.GetProperty("SnapshotId").GetString() != SnapshotArchive.Identity(snapshot))
            throw new InvalidOperationException("Approval document must name this exact frozen snapshot.");
        var exclusions = document.RootElement.TryGetProperty("Exclusions", out var value)
            ? value.Deserialize<Dictionary<int, ImportExclusion>>()! : [];
        var reconciliation = document.RootElement.TryGetProperty("Reconciliation", out var plan) ? plan.Deserialize<ImportReconciliation>() : null;
        return new() { Snapshot = snapshot.ToDomain(), Exclusions = exclusions, Reconciliation = reconciliation };
    }

    private static DatabaseOptions StagingDatabase()
    {
        var secret = RequiredEnvironment("GUITO_STAGING_DB_SECRET_ARN");
        if (!secret.StartsWith("arn:aws:secretsmanager:eu-west-1:497087877832:secret:guito-api/db-staging-", StringComparison.Ordinal))
            throw new InvalidOperationException("Database credential must be the staging secret.");
        return new() { DatabaseName = "guito_staging", SecretArn = secret, ResourceArn = "arn:aws:rds:eu-west-1:497087877832:cluster:db-cluster" };
    }

    private static (string SpreadsheetId, string Tab, int FirstRow) ReadSource(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetFullPath(root), "src/guito-api/appsettings.json")));
        var sheets = document.RootElement.GetProperty("AppConfiguration").GetProperty("Googlesheets");
        var anchor = sheets.GetProperty("ExpensesRange").GetString()!.Split('!');
        if (anchor.Length != 2 || !anchor[1].StartsWith('B')) throw new InvalidDataException("Expected frozen B:H schema.");
        return (sheets.GetProperty("SpreadsheetId").GetString()!, anchor[0], int.Parse(anchor[1][1..], CultureInfo.InvariantCulture));
    }

    private static async Task<FrozenSheetsSnapshot> LoadVerifiedAsync(string path)
    {
        var snapshot = await SnapshotArchive.LoadAsync(path);
        if (Path.GetFileName(path) != $"{SnapshotArchive.Identity(snapshot)}.snapshot.json") throw new InvalidDataException("Archive filename/content hash mismatch.");
        FrozenSnapshotMapper.Validate(snapshot);
        return snapshot;
    }

    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Required environment variable {name} is absent.");
}
