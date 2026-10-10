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

    private PostgresResult ExecuteExpense(string sql, IReadOnlyList<PostgresParameter>? parameters)
    {
        var values = parameters!.ToDictionary(p => p.Name, p => p.Value);
        if (sql.StartsWith("INSERT INTO expenses", StringComparison.Ordinal))
        {
            var now = PostgresValue.FromString(DateTimeOffset.UtcNow.ToString("O"));
            _expenses.Add([values["id"], values["occurred_at"], values["amount"], values["currency"],
                values["description"], values["category"], values["creator_email"], now, now,
                values.GetValueOrDefault("scope", PostgresValue.FromString("normal"))]);
            return new PostgresResult([], [], 1);
        }
        if (sql.StartsWith("SELECT", StringComparison.Ordinal))
        {
            var rows = _expenses.Where(r => r[9].StringValue == values["scope"].StringValue);
            if (sql.Contains("ORDER BY occurred_at DESC, id DESC", StringComparison.Ordinal))
                rows = rows.OrderByDescending(r => DateTimeOffset.Parse(r[1].StringValue!))
                    .ThenByDescending(r => r[0].StringValue, StringComparer.Ordinal);
            return new PostgresResult([], rows.Take((int)values["limit"].LongValue!).Select(r => r.Take(9).ToList()).ToList(), 0);
        }
        if (sql.StartsWith("DELETE", StringComparison.Ordinal))
        {
            var removed = _expenses.RemoveAll(r => r[0].StringValue == values["id"].StringValue
                && sql.Contains("scope = 'smoke'", StringComparison.Ordinal) && r[9].StringValue == "smoke");
            return new PostgresResult([], [], removed);
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
        if (PersistExpenses && sql.Contains("expenses", StringComparison.Ordinal))
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
