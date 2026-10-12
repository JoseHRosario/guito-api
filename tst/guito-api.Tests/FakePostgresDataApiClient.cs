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

    public void SeedExpense(int id, string bookingDate, string amount, string category, string creatorEmail = "human@example.com", string description = "Original purchase") =>
        _expenses.Add([PostgresValue.FromString(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            PostgresValue.FromString(bookingDate), PostgresValue.FromString(amount), PostgresValue.FromString(description),
            PostgresValue.FromString(category), PostgresValue.FromString(creatorEmail),
            PostgresValue.FromString("2027-02-01T10:00:00Z"), PostgresValue.Null()]);

    public void SeedExpense(IReadOnlyList<PostgresValue> row) => _expenses.Add(row);

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
            return InsertExpense(values);
        if (sql.Contains("LEFT JOIN LATERAL", StringComparison.Ordinal))
            return ReadExpenseHistory(sql, values);
        if (sql.StartsWith("SELECT", StringComparison.Ordinal))
        {
            return ReadExpenses(sql, values);
        }
        throw new InvalidOperationException("Unsupported expense statement in fake.");
    }
    private PostgresResult ReadExpenses(string sql, Dictionary<string, PostgresValue> values)
    {
        IEnumerable<IReadOnlyList<PostgresValue>> rows = _expenses;
        if (sql.Contains("e.category_id = :category_id", StringComparison.Ordinal))
            rows = rows.Where(r => ExpenseCategoryIds[r[4].StringValue!] == values["category_id"].LongValue);
        if (sql.Contains("e.booking_date >= CAST(:start AS timestamptz)", StringComparison.Ordinal))
            rows = rows.Where(r => DateTimeOffset.Parse(r[1].StringValue!) >= DateTimeOffset.Parse(values["start"].StringValue!));
        if (sql.Contains("e.booking_date < CAST(:end AS timestamptz)", StringComparison.Ordinal))
            rows = rows.Where(r => values["end"].StringValue == "10000-01-01T00:00:00+00:00" || DateTimeOffset.Parse(r[1].StringValue!) < DateTimeOffset.Parse(values["end"].StringValue!));
        if (sql.Contains("ORDER BY e.booking_date DESC, e.id DESC", StringComparison.Ordinal))
            rows = rows.OrderByDescending(r => DateTimeOffset.Parse(r[1].StringValue!))
                .ThenByDescending(r => int.Parse(r[0].StringValue!, System.Globalization.CultureInfo.InvariantCulture));
        if (sql.Contains("LIMIT :limit", StringComparison.Ordinal)) rows = rows.Take((int)values["limit"].LongValue!);
        return new PostgresResult([], rows.ToList(), 0);
    }

    private PostgresResult InsertExpense(Dictionary<string, PostgresValue> values)
    {
        var id = _expenses.Count + 1;
        var category = ExpenseCategoryIds.Single(c => c.Value == values["category_id"].LongValue).Key;
        var now = PostgresValue.FromString(DateTimeOffset.UtcNow.ToString("O"));
        _expenses.Add([PostgresValue.FromString(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            values["booking_date"], values["amount"], values["description"],
            PostgresValue.FromString(category), values["creator_email"], now, PostgresValue.Null()]);
        return new PostgresResult([], [[PostgresValue.FromLong(id)]], 1);
    }

    private PostgresResult ReadExpenseHistory(string sql, Dictionary<string, PostgresValue> values)
    {
        if (!sql.Contains("JOIN categories c ON c.id = e.category_id", StringComparison.Ordinal))
            throw new InvalidOperationException("History category lookup requires the mirror identity join.");
        IEnumerable<IReadOnlyList<PostgresValue>> rows = _expenses.Where(row => ExpenseCategoryIds.ContainsKey(row[4].StringValue!));
        if (sql.Contains("ORDER BY e.booking_date DESC", StringComparison.Ordinal))
            rows = rows.OrderByDescending(row => DateTimeOffset.Parse(row[1].StringValue!))
                .ThenByDescending(row => sql.Contains("e.id DESC", StringComparison.Ordinal)
                    ? int.Parse(row[0].StringValue!) : 0);
        if (sql.Contains("OFFSET :offset", StringComparison.Ordinal))
            rows = rows.Skip((int)Math.Min(values["offset"].LongValue!.Value, int.MaxValue));
        if (sql.Contains("LIMIT :limit", StringComparison.Ordinal))
            rows = rows.Take((int)values["limit"].LongValue!.Value);
        var total = sql.Contains("SELECT COUNT(*) AS total_count FROM expenses", StringComparison.Ordinal) ? _expenses.Count : 0;
        var count = PostgresValue.FromString(total.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var result = rows.Select(row => (IReadOnlyList<PostgresValue>)new[] { count }.Concat(row).ToArray()).ToList();
        if (result.Count == 0) result.Add(new[] { count }.Concat(Enumerable.Repeat(PostgresValue.Null(), 8)).ToArray());
        return new PostgresResult([], result, 0);
    }

    public Exception? ExecuteException { get; set; }
    public int ExecuteExceptionAtCall { get; set; } = 1;
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
        if (ExecuteException is not null && ExecutedSql.Count >= ExecuteExceptionAtCall)
            throw ExecuteException;
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
