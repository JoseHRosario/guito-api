using System.Text.Json;
using GuitoApi.Infrastructure.Postgres;
namespace GuitoApi.Tests;

public sealed class ImportDatabaseTransport : IPostgresDataApiClient
{
    public PostgresTransactionContext Context { get; } = new();
    public Dictionary<string, string> Heads { get; private set; } = [];
    private Dictionary<string, Ledger> _rows = [];
    private Dictionary<int, IReadOnlyList<PostgresValue>> _expenses = [];
    private Dictionary<string, string> _backupHeads = [];
    private Dictionary<string, Ledger> _backupRows = [];
    private Dictionary<int, IReadOnlyList<PostgresValue>> _backupExpenses = [];
    private Dictionary<string, string> _plans = [];
    private Dictionary<string, string> _backupPlans = [];
    private int _sequence;
    public int ExpenseCount => _expenses.Count;
    public string? FailDescription { get; set; }
    public bool LoseCommitAcknowledgement { get; set; }
    public bool CorruptAmount { get; set; }
    public int Rollbacks { get; private set; }
    public Dictionary<string, long> Categories { get; } = new() { ["Restaurants"] = 2, ["Test"] = 2147483648L };
    private sealed record Ledger(string Raw, string Classification, int? ExpenseId = null, string? Exclusion = null);

    public Task<PostgresResult> ExecuteAsync(string sql, IReadOnlyList<PostgresParameter>? parameters = null, string? transactionId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Context.CurrentTransactionId == null) throw new InvalidOperationException("Import write/read is outside a unit of work.");
        var values = parameters!.ToDictionary(p => p.Name, p => p.Value);
        return Task.FromResult(ExecuteStatement(sql, values));
    }

    private PostgresResult ExecuteStatement(string sql, Dictionary<string, PostgresValue> values) => sql switch
    {
        var s when s.Contains("pg_advisory_xact_lock") => Result(),
        var s when s.Contains("expense_import_sources") || s.Contains("expense_import_snapshots") => ExecuteSnapshot(sql, values),
        var s when s.StartsWith("INSERT INTO expense_import_rows") => ArchiveRows(values),
        var s when s.StartsWith("SELECT CAST(raw_json") => ReadRow(values),
        var s when s.StartsWith("SELECT source_row") => ReadPrevious(values),
        var s when s.StartsWith("UPDATE expense_import_rows") => UpdateRow(sql, values),
        var s when s.StartsWith("SELECT id FROM categories") => Categories.TryGetValue(Text(values, "name"), out var id) ? Result([PostgresValue.FromLong(id)]) : Result(),
        var s when s.StartsWith("INSERT INTO expenses") => CreateExpense(values),
        var s when s.StartsWith("SELECT e.id") => ReadExpense(values),
        _ => throw new InvalidOperationException($"Unsupported import transport statement: {sql}")
    };

    private PostgresResult ExecuteSnapshot(string sql, Dictionary<string, PostgresValue> values)
    {
        if (sql.StartsWith("SELECT current_snapshot_id"))
            return Heads.TryGetValue(Text(values, "source"), out var head) ? Result([PostgresValue.FromString(head)]) : Result();
        if (sql.StartsWith("SELECT CAST(reconciliation"))
            return _plans.TryGetValue(Text(values, "snapshot"), out var plan) ? Result([PostgresValue.FromString(plan)]) : Result();
        if (sql.StartsWith("INSERT INTO expense_import_sources")) Heads[Text(values, "source")] = Text(values, "snapshot");
        else _plans.TryAdd(Text(values, "snapshot"), Text(values, "reconciliation"));
        return Result();
    }

    private PostgresResult ArchiveRows(Dictionary<string, PostgresValue> values)
    {
        using var document = JsonDocument.Parse(Text(values, "rows"));
        foreach (var row in document.RootElement.EnumerateArray())
            _rows.TryAdd($"{Text(values, "snapshot")}:{row.GetProperty("source_row").GetInt32()}",
                new(row.GetProperty("raw_json").GetRawText(), row.GetProperty("classification").GetString()!));
        return Result();
    }

    private PostgresResult ReadRow(Dictionary<string, PostgresValue> values)
    {
        var row = _rows[RowKey(values)];
        return Result([PostgresValue.FromString(row.Raw), OptionalId(row.ExpenseId), OptionalText(row.Exclusion)]);
    }

    private PostgresResult ReadPrevious(Dictionary<string, PostgresValue> values) =>
        Result(_rows.Where(r => r.Key.StartsWith($"{Text(values, "snapshot")}:") && r.Value.Classification is not ("Blank" or "NonExpense")
            && SourceRow(r.Key) > values["cursor"].LongValue).OrderBy(r => SourceRow(r.Key)).Take(100)
            .Select(r => (IReadOnlyList<PostgresValue>)new[] { PostgresValue.FromLong(SourceRow(r.Key)),
                PostgresValue.FromString(r.Value.Raw), OptionalId(r.Value.ExpenseId), OptionalText(r.Value.Exclusion) }).ToArray());

    private PostgresResult UpdateRow(string sql, Dictionary<string, PostgresValue> values)
    {
        var key = RowKey(values);
        if (sql.StartsWith("UPDATE expense_import_rows SET exclusion"))
        { _rows[key] = _rows[key] with { Exclusion = Text(values, "exclusion") }; return Result(); }
        if (_rows.Any(r => r.Key.StartsWith($"{Text(values, "snapshot")}:") && r.Key != key && r.Value.ExpenseId == Number(values, "expense")))
            throw new InvalidOperationException("Unique snapshot expense violated.");
        _rows[key] = _rows[key] with { ExpenseId = Number(values, "expense") };
        return Result();
    }

    private PostgresResult CreateExpense(Dictionary<string, PostgresValue> values)
    {
        if (Text(values, "description") == FailDescription) throw new InvalidOperationException("Simulated interrupted row write.");
        var id = ++_sequence;
        _expenses[id] = [PostgresValue.FromLong(id), values["booking_date"], values["amount"], values["description"],
            PostgresValue.FromString(Categories.Single(c => c.Value == values["category_id"].LongValue).Key), values["creator_email"],
            PostgresValue.FromString("2026-10-11T12:00:00Z"), PostgresValue.Null()];
        return Result([PostgresValue.FromLong(id)]);
    }

    private PostgresResult ReadExpense(Dictionary<string, PostgresValue> values)
    {
        var row = _expenses[Number(values, "expense")].ToArray();
        if (CorruptAmount) row[2] = PostgresValue.FromString("999");
        return Result(row);
    }

    private static PostgresResult Result(params IReadOnlyList<PostgresValue>[] rows) => new([], rows, 0);
    private static string Text(Dictionary<string, PostgresValue> values, string key) => values[key].StringValue!;
    private static int Number(Dictionary<string, PostgresValue> values, string key) => checked((int)values[key].LongValue!);
    private static string RowKey(Dictionary<string, PostgresValue> values) => $"{Text(values, "snapshot")}:{Number(values, "row")}";
    private static int SourceRow(string key) => int.Parse(key.Split(':').Last());
    private static PostgresValue OptionalId(int? id) => id is { } value ? PostgresValue.FromLong(value) : PostgresValue.Null();
    private static PostgresValue OptionalText(string? text) => text is { } value ? PostgresValue.FromString(value) : PostgresValue.Null();

    public Task<string> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        _backupPlans = new(_plans); _backupHeads = new(Heads); _backupRows = new(_rows); _backupExpenses = new(_expenses);
        return Task.FromResult("import-transaction");
    }
    public Task CommitAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        if (LoseCommitAcknowledgement && _expenses.Count > 0) { LoseCommitAcknowledgement = false; throw new IOException("Commit acknowledged by server, response lost."); }
        return Task.CompletedTask;
    }
    public Task RollbackAsync(string transactionId, CancellationToken cancellationToken = default)
    { _plans = _backupPlans; Heads = _backupHeads; _rows = _backupRows; _expenses = _backupExpenses; Rollbacks++; return Task.CompletedTask; }
}
