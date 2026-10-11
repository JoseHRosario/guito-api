using GuitoApi.Infrastructure.Postgres;

namespace GuitoApi.Tests;

/// <summary>
/// Hermetic fake of IPostgresDataApiClient for direct repository tests (issue #88).
/// Scripts result sets/exceptions via NextResults/ExecuteException and records every
/// executed statement (SQL, parameters, transaction id, cancellation token).
/// </summary>
public class FakePostgresDataApiClient : IPostgresDataApiClient
{
    public Queue<PostgresResult> NextResults { get; } = new();
    public bool PersistExpenses { get; set; }
    private readonly List<IReadOnlyList<PostgresValue>> _expenses = [];
    private readonly Dictionary<string, (DateTimeOffset Expiry, long TotalCount, List<IReadOnlyList<PostgresValue>> Rows)> _historySnapshots = [];
    private readonly Dictionary<string, (string Snapshot, long Position, long PageSize)> _historyCursors = [];
    public DateTimeOffset DatabaseNow { get; set; } = DateTimeOffset.UtcNow;
    private readonly List<IReadOnlyList<PostgresValue>> _uncommittedExpenses = [];

    public void SeedExpense(IReadOnlyList<PostgresValue> row, bool committed = true) =>
        (committed ? _expenses : _uncommittedExpenses).Add(row);

    public void CommitStagedExpenses()
    {
        _expenses.AddRange(_uncommittedExpenses);
        _uncommittedExpenses.Clear();
    }

    public void ReplaceStoredExpense(int id, IReadOnlyList<PostgresValue> row) =>
        _expenses[_expenses.FindIndex(e => int.Parse(e[0].StringValue!) == id)] = row;

    private PostgresResult ExecuteHistory(string sql, IReadOnlyList<PostgresParameter> parameters)
    {
        var values = parameters.ToDictionary(p => p.Name, p => p.Value);
        if (sql.Contains("source AS MATERIALIZED", StringComparison.Ordinal))
        {
            if (sql.Contains("DELETE FROM expense_history_rows", StringComparison.Ordinal))
                foreach (var expired in _historySnapshots.Values.Where(s => s.Expiry <= DatabaseNow)) expired.Rows.Clear();
            var snapshotId = values["snapshot_id"].StringValue!;
            var rows = sql.Contains("ORDER BY e.booking_date DESC, e.id DESC", StringComparison.Ordinal)
                ? _expenses.OrderByDescending(r => DateTimeOffset.Parse(r[1].StringValue!))
                    .ThenByDescending(r => int.Parse(r[0].StringValue!)).ToList() : _expenses.ToList();
            var expiry = DatabaseNow.AddMinutes(30);
            _historySnapshots.Add(snapshotId, (expiry, rows.Count, rows));
            return new([], [[PostgresValue.FromString(snapshotId), PostgresValue.FromString(expiry.ToString("O")),
                PostgresValue.FromLong(rows.Count), PostgresValue.FromLong(rows.Count)]], 0);
        }
        if (sql.StartsWith("INSERT INTO expense_history_cursors", StringComparison.Ordinal))
        {
            var state = (values["snapshot_id"].StringValue!, values["start_position"].LongValue!.Value,
                values["page_size"].LongValue!.Value);
            var token = _historyCursors.FirstOrDefault(c => c.Value == state).Key ?? values["token"].StringValue!;
            _historyCursors[token] = state;
            return new([], [[PostgresValue.FromString(token)]], 1);
        }
        if (sql.Contains("FROM expense_history_cursors", StringComparison.Ordinal))
        {
            if (!_historyCursors.TryGetValue(values["token"].StringValue!, out var cursor)) return new([], [], 0);
            var snapshot = _historySnapshots[cursor.Snapshot];
            return new([], [[PostgresValue.FromString(cursor.Snapshot), PostgresValue.FromString(snapshot.Expiry.ToString("O")),
                PostgresValue.FromLong(snapshot.TotalCount), PostgresValue.FromLong(cursor.Position),
                PostgresValue.FromLong(cursor.PageSize), PostgresValue.FromString((sql.Contains("s.expires_at <= now()", StringComparison.Ordinal)
                    && snapshot.Expiry <= DatabaseNow).ToString().ToLowerInvariant())]], 0);
        }
        if (sql.Contains("FROM expense_history_rows", StringComparison.Ordinal))
        {
            var rows = sql.Contains("snapshot_id = CAST(:snapshot_id AS uuid)", StringComparison.Ordinal)
                ? _historySnapshots[values["snapshot_id"].StringValue!].Rows
                : _historySnapshots.Values.SelectMany(s => s.Rows).ToList();
            IEnumerable<IReadOnlyList<PostgresValue>> page = rows;
            if (sql.Contains("position > :start_position", StringComparison.Ordinal)) page = page.Skip((int)values["start_position"].LongValue!.Value);
            if (!sql.Contains("ORDER BY position ASC", StringComparison.Ordinal)) page = page.Reverse();
            if (sql.Contains("LIMIT :page_size", StringComparison.Ordinal)) page = page.Take((int)values["page_size"].LongValue!.Value);
            return new([], page.Select(r => (IReadOnlyList<PostgresValue>)[PostgresValue.FromLong(long.Parse(r[0].StringValue!)),
                r[1], r[2], r[3], r[4], r[5], r[6], r[7]]).ToList(), 0);
        }
        throw new InvalidOperationException("Unsupported history statement in fake.");
    }

    public Dictionary<string, long> ExpenseCategoryIds { get; } = new(StringComparer.Ordinal)
    {
        ["Test"] = 2147483648L, ["Restaurants"] = 2, ["Groceries"] = 3, ["Original category"] = 4
    };

    private PostgresResult ExecuteExpense(string sql, IReadOnlyList<PostgresParameter>? parameters)
    {
        var values = parameters!.ToDictionary(p => p.Name, p => p.Value);
        if (sql == "SELECT id FROM categories WHERE name = :name")
            return ExpenseCategoryIds.TryGetValue(values["name"].StringValue!, out var categoryId)
                ? new PostgresResult([], [[PostgresValue.FromLong(categoryId)]], 0)
                : new PostgresResult([], [], 0);
        if (sql.StartsWith("INSERT INTO expenses", StringComparison.Ordinal))
        {
            var id = _expenses.Concat(_uncommittedExpenses).Select(e => int.Parse(e[0].StringValue!)).DefaultIfEmpty().Max() + 1;
            var category = ExpenseCategoryIds.Single(c => c.Value == values["category_id"].LongValue).Key;
            var now = PostgresValue.FromString(DateTimeOffset.UtcNow.ToString("O"));
            _expenses.Add([PostgresValue.FromString(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                values["booking_date"], values["amount"], values["description"],
                PostgresValue.FromString(category), values["creator_email"], now, PostgresValue.Null()]);
            return new PostgresResult([], [[PostgresValue.FromLong(id)]], 1);
        }
        if (sql.StartsWith("SELECT", StringComparison.Ordinal))
        {
            var rows = _expenses.OrderByDescending(r => DateTimeOffset.Parse(r[1].StringValue!))
                .ThenByDescending(r => int.Parse(r[0].StringValue!, System.Globalization.CultureInfo.InvariantCulture));
            return new PostgresResult([], rows.Take((int)values["limit"].LongValue!).ToList(), 0);
        }
        throw new InvalidOperationException("Unsupported expense statement in fake.");
    }
    public Exception? ExecuteException { get; set; }
    public string? NextTransactionId { get; set; }

    public List<string> ExecutedSql { get; } = [];
    public List<IReadOnlyList<PostgresParameter>?> ExecutedParameters { get; } = [];
    public List<string?> ExecutedTransactionIds { get; } = [];
    public List<CancellationToken> ExecutedCancellationTokens { get; } = [];

    public Task<PostgresResult> ExecuteAsync(
        string sql,
        IReadOnlyList<PostgresParameter>? parameters = null,
        string? transactionId = null,
        CancellationToken cancellationToken = default)
    {
        ExecutedSql.Add(sql);
        ExecutedParameters.Add(parameters);
        ExecutedTransactionIds.Add(transactionId);
        ExecutedCancellationTokens.Add(cancellationToken);
        if (ExecuteException is not null)
            throw ExecuteException;
        if (PersistExpenses && NextResults.Count == 0 && sql.Contains("expense_history_", StringComparison.Ordinal))
            return Task.FromResult(ExecuteHistory(sql, parameters!));
        if (PersistExpenses && NextResults.Count == 0 && (sql.Contains("expenses", StringComparison.Ordinal)
            || sql == "SELECT id FROM categories WHERE name = :name"))
            return Task.FromResult(ExecuteExpense(sql, parameters));
        return Task.FromResult(NextResults.Count > 0
            ? NextResults.Dequeue()
            : new PostgresResult([], [], 0));
    }

    public Task<string> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(NextTransactionId ?? throw new InvalidOperationException("No transaction id scripted."));

    public Task CommitAsync(string transactionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RollbackAsync(string transactionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
