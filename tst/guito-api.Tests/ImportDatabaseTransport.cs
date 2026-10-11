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
        string Text(string key) => values[key].StringValue!;
        int Number(string key) => checked((int)values[key].LongValue!);
        var key = values.ContainsKey("row") ? Text("snapshot") + ":" + Number("row") : "";
        PostgresResult Result(params IReadOnlyList<PostgresValue>[] rows) => new([], rows, 0);
        var result = Result();
        if (sql.Contains("pg_advisory_xact_lock")) return Task.FromResult(result);
        if (sql.StartsWith("SELECT current_snapshot_id")) result = Heads.TryGetValue(Text("source"), out var head) ? Result([PostgresValue.FromString(head)]) : result;
        else if (sql.StartsWith("INSERT INTO expense_import_sources")) Heads[Text("source")] = Text("snapshot");
        else if (sql.StartsWith("SELECT CAST(reconciliation")) result = _plans.TryGetValue(Text("snapshot"), out var plan) ? Result([PostgresValue.FromString(plan)]) : result;
        else if (sql.StartsWith("INSERT INTO expense_import_snapshots")) _plans.TryAdd(Text("snapshot"), Text("reconciliation"));
        else if (sql.StartsWith("INSERT INTO expense_import_rows"))
        {
            foreach (var row in JsonDocument.Parse(Text("rows")).RootElement.EnumerateArray())
                _rows.TryAdd(Text("snapshot") + ":" + row.GetProperty("source_row").GetInt32(), new(row.GetProperty("raw_json").GetRawText(), row.GetProperty("classification").GetString()!));
        }
        else if (sql.StartsWith("SELECT CAST(raw_json"))
        {
            var row = _rows[key];
            result = Result([PostgresValue.FromString(row.Raw), row.ExpenseId is { } id ? PostgresValue.FromLong(id) : PostgresValue.Null(), row.Exclusion is { } exclusion ? PostgresValue.FromString(exclusion) : PostgresValue.Null()]);
        }
        else if (sql.StartsWith("SELECT source_row"))
            result = Result(_rows.Where(r => r.Key.StartsWith(Text("snapshot") + ":") && r.Value.Classification is not ("Blank" or "NonExpense") && int.Parse(r.Key.Split(':').Last()) > values["cursor"].LongValue).OrderBy(r => int.Parse(r.Key.Split(':').Last())).Take(100).Select(r => (IReadOnlyList<PostgresValue>)new[] { PostgresValue.FromLong(int.Parse(r.Key.Split(':').Last())), PostgresValue.FromString(r.Value.Raw), r.Value.ExpenseId is { } previousId ? PostgresValue.FromLong(previousId) : PostgresValue.Null(), r.Value.Exclusion is { } previousExclusion ? PostgresValue.FromString(previousExclusion) : PostgresValue.Null() }).ToArray());
        else if (sql.StartsWith("UPDATE expense_import_rows SET expense_id"))
        {
            if (_rows.Any(r => r.Key.StartsWith(Text("snapshot") + ":") && r.Key != key && r.Value.ExpenseId == Number("expense"))) throw new InvalidOperationException("Unique snapshot expense violated.");
            _rows[key] = _rows[key] with { ExpenseId = Number("expense") };
        }
        else if (sql.StartsWith("UPDATE expense_import_rows SET exclusion")) _rows[key] = _rows[key] with { Exclusion = Text("exclusion") };
        else if (sql.StartsWith("SELECT id FROM categories")) result = Categories.TryGetValue(Text("name"), out var categoryId) ? Result([PostgresValue.FromLong(categoryId)]) : result;
        else if (sql.StartsWith("INSERT INTO expenses"))
        {
            if (Text("description") == FailDescription) throw new InvalidOperationException("Simulated interrupted row write.");
            var id = ++_sequence;
            _expenses[id] = [PostgresValue.FromLong(id), values["booking_date"], values["amount"], values["description"], PostgresValue.FromString(Categories.Single(c => c.Value == values["category_id"].LongValue).Key), values["creator_email"], PostgresValue.FromString("2026-10-11T12:00:00Z"), PostgresValue.Null()];
            result = Result([PostgresValue.FromLong(id)]);
        }
        else if (sql.StartsWith("SELECT e.id"))
        {
            var row = _expenses[Number("expense")].ToArray();
            if (CorruptAmount) row[2] = PostgresValue.FromString("999");
            result = Result(row);
        }
        else throw new InvalidOperationException("Unsupported import transport statement: " + sql);
        return Task.FromResult(result);
    }

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
