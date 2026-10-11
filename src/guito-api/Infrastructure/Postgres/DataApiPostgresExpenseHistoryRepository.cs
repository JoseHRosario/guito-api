using System.Globalization;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;
using GuitoApi.Services;

namespace GuitoApi.Infrastructure.Postgres;

public sealed class DataApiPostgresExpenseHistoryRepository(IPostgresDataApiClient client) : IExpenseHistoryRepository
{
    // A single statement's MVCC view copies membership AND values; sequence/timestamp cutoffs
    // cannot exclude a lower-ID transaction which commits after the export starts.
    private const string CreateSnapshotSql = """
        WITH expired_rows AS (
            DELETE FROM expense_history_rows
            WHERE snapshot_id IN (SELECT id FROM expense_history_snapshots WHERE expires_at <= now())
        ), source AS MATERIALIZED (
            SELECT e.id, e.booking_date, e.create_date, e.update_date, e.amount::text AS amount,
                   e.description, c.name AS category, e.creator_email,
                   row_number() OVER (ORDER BY e.booking_date DESC, e.id DESC) AS position
            FROM expenses e JOIN categories c ON c.id = e.category_id
        ), snapshot AS (
            INSERT INTO expense_history_snapshots (id, expires_at, total_count)
            SELECT CAST(:snapshot_id AS uuid), now() + interval '30 minutes', count(*) FROM source
            RETURNING id, expires_at, total_count
        ), copied AS (
            INSERT INTO expense_history_rows
                (snapshot_id, position, expense_id, booking_date, create_date, update_date,
                 amount, description, category, creator_email)
            SELECT s.id, e.position, e.id, e.booking_date, e.create_date, e.update_date,
                   e.amount, e.description, e.category, e.creator_email
            FROM source e CROSS JOIN snapshot s RETURNING position
        )
        SELECT s.id::text, s.expires_at::text, s.total_count, (SELECT count(*) FROM copied)
        FROM snapshot s
        """;
    private const string CursorSql = """
        SELECT s.id::text, s.expires_at::text, s.total_count, c.start_position, c.page_size,
               (s.expires_at <= now())::text
        FROM expense_history_cursors c JOIN expense_history_snapshots s ON s.id = c.snapshot_id
        WHERE c.token = CAST(:token AS uuid)
        """;
    private const string PageSql = """
        SELECT p.expense_id, p.booking_date::text, p.amount, p.description, p.category, p.creator_email,
               p.create_date::text, p.update_date::text, (s.expires_at <= statement_timestamp())::text
        FROM expense_history_snapshots s
        LEFT JOIN LATERAL (
            SELECT expense_id, booking_date, amount, description, category, creator_email,
                   create_date, update_date, position
            FROM expense_history_rows
            WHERE snapshot_id = s.id AND position > :start_position
              AND s.expires_at > statement_timestamp()
            ORDER BY position ASC LIMIT :page_size
        ) p ON true
        WHERE s.id = CAST(:snapshot_id AS uuid)
        ORDER BY p.position ASC
        """;
    private const string NextCursorSql = """
        INSERT INTO expense_history_cursors (token, snapshot_id, start_position, page_size)
        VALUES (CAST(:token AS uuid), CAST(:snapshot_id AS uuid), :start_position, :page_size)
        ON CONFLICT (snapshot_id, start_position, page_size)
        DO UPDATE SET token = expense_history_cursors.token RETURNING token::text
        """;

    public async Task<ExpenseHistoryPage> ReadAsync(int pageSize, string? cursor, CancellationToken cancellationToken = default)
    {
        var header = ToHeader(cursor is null
            ? await CreateSnapshotAsync(cancellationToken)
            : await ReadCursorAsync(cursor, pageSize, cancellationToken), cursor is not null);
        var entries = await ReadPageAsync(header, pageSize, cancellationToken);
        var nextPosition = header.Position + entries.Count;
        var nextCursor = nextPosition < header.TotalCount
            ? await CreateCursorAsync(header.SnapshotId, nextPosition, pageSize, cancellationToken) : null;
        return new(header.SnapshotId, header.ExpiresAt, header.TotalCount, entries, nextCursor);
    }

    private async Task<List<ExpenseHistoryEntry>> ReadPageAsync(PostgresExpenseHistoryHeader header, int pageSize, CancellationToken cancellationToken)
    {
        // Header expiry and payload share one MVCC view: cleanup cannot turn an expired cursor into a partial page.
        var result = await client.ExecuteAsync(PageSql,
            [Text("snapshot_id", header.SnapshotId.ToString()), Number("start_position", header.Position), Number("page_size", pageSize)],
            cancellationToken: cancellationToken);
        if (result.Rows.Any(row => row[8].StringValue == "true"))
            throw new ProblemException(410, "Expense history cursor expired; start a new export.");
        var rows = result.Rows.Where(row => !row[0].IsNull).ToList();
        if (result.Rows.Count == 0 || rows.Count != Math.Min(pageSize, header.TotalCount - header.Position))
            throw new ProblemException(502, "Expense snapshot page was incomplete; restart the export.");
        return rows.Select(ToEntry).ToList();
    }

    private static PostgresExpenseHistoryHeader ToHeader(IReadOnlyList<PostgresValue> row, bool continuation) =>
        new(Guid.Parse(row[0].StringValue!), ReadInstant(row[1]), row[2].LongValue!.Value,
            continuation ? row[3].LongValue!.Value : 0);

    private async Task<IReadOnlyList<PostgresValue>> CreateSnapshotAsync(CancellationToken cancellationToken)
    {
        var result = await client.ExecuteAsync(CreateSnapshotSql,
            [Text("snapshot_id", Guid.NewGuid().ToString())], cancellationToken: cancellationToken);
        return result.Rows.Single();
    }

    private async Task<IReadOnlyList<PostgresValue>> ReadCursorAsync(string cursor, int pageSize, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(cursor, "D", out _))
            throw new ProblemException(400, "Invalid Expense history cursor.");
        var result = await client.ExecuteAsync(CursorSql, [Text("token", cursor)], cancellationToken: cancellationToken);
        if (result.Rows.Count == 0)
            throw new ProblemException(400, "Unknown Expense history cursor.");
        var header = result.Rows.Single();
        if (header[5].StringValue == "true")
            throw new ProblemException(410, "Expense history cursor expired; start a new export.");
        if (header[4].LongValue != pageSize)
            throw new ProblemException(400, "History pageSize must remain unchanged across pages.");
        return header;
    }

    private async Task<string> CreateCursorAsync(Guid snapshotId, long position, int pageSize, CancellationToken cancellationToken)
    {
        var result = await client.ExecuteAsync(NextCursorSql,
            [Text("token", Guid.NewGuid().ToString()), Text("snapshot_id", snapshotId.ToString()),
             Number("start_position", position), Number("page_size", pageSize)], cancellationToken: cancellationToken);
        return result.Rows.Single()[0].StringValue!;
    }

    private static ExpenseHistoryEntry ToEntry(IReadOnlyList<PostgresValue> row) => new(
        checked((int)row[0].LongValue!.Value), ReadInstant(row[1]), ReadInstant(row[6]),
        row[7].IsNull ? null : ReadInstant(row[7]), row[2].StringValue!, "EUR", row[3].StringValue!,
        row[4].StringValue!, row[5].StringValue!);

    private static DateTimeOffset ReadInstant(PostgresValue value) =>
        TimeService.InLisbon(DateTimeOffset.Parse(value.StringValue!, CultureInfo.InvariantCulture));
    private static PostgresParameter Text(string name, string value) => new(name, PostgresValue.FromString(value));
    private static PostgresParameter Number(string name, long value) => new(name, PostgresValue.FromLong(value));
}
