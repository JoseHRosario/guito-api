using System.Globalization;
using System.Text.Json;
using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Infrastructure.Serialization;
using GuitoApi.Model;
using GuitoApi.Repositories;
using GuitoApi.Services;
namespace GuitoApi.Infrastructure.Import;

public sealed class DataApiHistoricalImportRepository(IPostgresDataApiClient client, IUnitOfWork unit) : IHistoricalImportRepository
{
    public async Task RegisterAsync(string snapshotId, HistoricalImportRequest request, IReadOnlyList<HistoricalImportRow> rows, CancellationToken cancellationToken)
    {
        await TransactionAsync(async () =>
        {
            var source = SourceKey(request.Snapshot);
            await LockAsync(source, cancellationToken);
            ValidateExclusions(request, rows);
            await ValidateStoredPlanAsync(snapshotId, request, cancellationToken);
            var head = await HeadAsync(source, cancellationToken);
            if (head != null && head != snapshotId) await ValidateReconciliationAsync(head, request, rows, cancellationToken);
            await ExecuteAsync("INSERT INTO expense_import_sources (source_key, current_snapshot_id) VALUES (:source, :snapshot) ON CONFLICT (source_key) DO UPDATE SET current_snapshot_id = EXCLUDED.current_snapshot_id",
                [Text("source", source), Text("snapshot", snapshotId)], cancellationToken);
            await SaveSnapshotAsync(snapshotId, source, request, cancellationToken);
            foreach (var chunk in rows.Chunk(100)) await ArchiveRowsAsync(snapshotId, chunk, cancellationToken);
            return true;
        }, cancellationToken);
    }

    private async Task ValidateStoredPlanAsync(string id, HistoricalImportRequest request, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync("SELECT CAST(reconciliation AS text) FROM expense_import_snapshots WHERE id = :snapshot", [Text("snapshot", id)], cancellationToken);
        if (result.Rows.Count > 0 && !SameJson(result.Rows[0][0].StringValue ?? "null", JsonSerializer.Serialize(request.Reconciliation)))
            throw new InvalidOperationException("Retry must supply the same archived reconciliation plan.");
    }

    private static void ValidateExclusions(HistoricalImportRequest request, IReadOnlyList<HistoricalImportRow> rows)
    {
        foreach (var exclusion in request.Exclusions)
        {
            ValidateApproval(exclusion.Value.ApprovedBy, exclusion.Value.Reason);
            if (!rows.Any(r => r.SourceRow == exclusion.Key && r.Status is not ("Blank" or "NonExpense")))
                throw new InvalidOperationException("Exclusion must name a relevant row in this exact snapshot.");
        }
    }

    private async Task SaveSnapshotAsync(string id, string source, HistoricalImportRequest request, CancellationToken cancellationToken)
    {
        var snapshot = request.Snapshot;
        var metadata = JsonSerializer.Serialize(new { snapshot.SpreadsheetId, snapshot.Tab, snapshot.FirstRow, snapshot.LastRow, snapshot.Currency, snapshot.DecimalSeparator, snapshot.DateFormat });
        await ExecuteAsync("INSERT INTO expense_import_snapshots (id, source_key, metadata, reconciliation, captured_at) VALUES (:snapshot, :source, CAST(:metadata AS jsonb), CAST(:reconciliation AS jsonb), CAST(:captured AS timestamptz)) ON CONFLICT (id) DO NOTHING",
            [Text("snapshot", id), Text("source", source), Text("metadata", metadata), Text("reconciliation", JsonSerializer.Serialize(request.Reconciliation)), Text("captured", snapshot.CapturedAt.ToString("O"))], cancellationToken);
    }

    private Task<PostgresResult> ArchiveRowsAsync(string id, HistoricalImportRow[] rows, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(rows.Select(r => new { source_row = r.SourceRow, raw_json = JsonDocument.Parse(r.RawJson).RootElement, classification = r.Status }));
        return ExecuteAsync("INSERT INTO expense_import_rows (snapshot_id, source_row, raw_json, classification) SELECT :snapshot, source_row, raw_json, classification FROM jsonb_to_recordset(CAST(:rows AS jsonb)) AS r(source_row integer, raw_json jsonb, classification text) ON CONFLICT (snapshot_id, source_row) DO NOTHING",
            [Text("snapshot", id), Text("rows", data)], cancellationToken);
    }

    public Task<ExpenseSummary?> ImportRowAsync(string snapshotId, HistoricalImportRow row, HistoricalImportRequest request, CancellationToken cancellationToken) =>
        TransactionAsync(async () =>
        {
            var source = SourceKey(request.Snapshot);
            await LockAsync(source, cancellationToken);
            if (await HeadAsync(source, cancellationToken) != snapshotId) throw new InvalidOperationException("Snapshot superseded; reconcile against the current source head.");
            var stored = await ReadRowAsync(snapshotId, row.SourceRow, cancellationToken);
            if (!SameJson(stored[0].StringValue!, row.RawJson)) throw new InvalidOperationException("Frozen row provenance mismatch.");
            if (!stored[1].IsNull) return await ReadExpenseAsync(checked((int)ReadLong(stored[1])), cancellationToken);
            if (!stored[2].IsNull)
            { row.Exclusion = JsonSerializer.Deserialize<ImportExclusion>(stored[2].StringValue!); return null; }
            if (request.Exclusions.TryGetValue(row.SourceRow, out var exclusion)) return await ExcludeAsync(snapshotId, row, exclusion, cancellationToken);
            if (row.Expected == null) throw new InvalidOperationException(row.Exception ?? "Malformed expense requires an explicit approved exclusion.");
            var id = await ResolveExpenseAsync(row, request, cancellationToken);
            await ExecuteAsync("UPDATE expense_import_rows SET expense_id = :expense WHERE snapshot_id = :snapshot AND source_row = :row",
                [Text("snapshot", snapshotId), Number("row", row.SourceRow), Number("expense", id)], cancellationToken);
            return await ReadExpenseAsync(id, cancellationToken);
        }, cancellationToken);

    private async Task<int> ResolveExpenseAsync(HistoricalImportRow row, HistoricalImportRequest request, CancellationToken cancellationToken)
    {
        if (request.Reconciliation?.ReuseRows.TryGetValue(row.SourceRow, out var previousRow) == true)
        {
            var previous = await ReadRowAsync(request.Reconciliation.PreviousSnapshotId, previousRow, cancellationToken);
            if (previous[1].IsNull || !SameJson(previous[0].StringValue!, row.RawJson)) throw new InvalidOperationException("Reused row must match its imported predecessor exactly.");
            return checked((int)ReadLong(previous[1]));
        }
        return await new DataApiPostgresExpenseRepository(client).CreateAsync(row.Expected!, cancellationToken);
    }

    private async Task<ExpenseSummary?> ExcludeAsync(string id, HistoricalImportRow row, ImportExclusion exclusion, CancellationToken cancellationToken)
    {
        row.Exclusion = exclusion;
        ValidateApproval(exclusion.ApprovedBy, exclusion.Reason);
        await ExecuteAsync("UPDATE expense_import_rows SET exclusion = CAST(:exclusion AS jsonb) WHERE snapshot_id = :snapshot AND source_row = :row",
            [Text("snapshot", id), Number("row", row.SourceRow), Text("exclusion", JsonSerializer.Serialize(exclusion))], cancellationToken);
        return null;
    }

    private async Task ValidateReconciliationAsync(string head, HistoricalImportRequest request, IReadOnlyList<HistoricalImportRow> rows, CancellationToken cancellationToken)
    {
        var plan = request.Reconciliation ?? throw new InvalidOperationException($"Changed snapshot detected. Current snapshot is {head}; explicit reconciliation is required before any insert.");
        ValidateApproval(plan.ApprovedBy, plan.Reason);
        if (plan.PreviousSnapshotId != head) throw new InvalidOperationException("Reconciliation does not name the current predecessor.");
        var relevant = rows.Where(r => r.Status is not ("Blank" or "NonExpense")).Select(r => r.SourceRow).Order().ToArray();
        var decisions = plan.ReuseRows.Keys.Concat(plan.NewRows).Concat(request.Exclusions.Keys).ToArray();
        if (decisions.Length != decisions.Distinct().Count() || !relevant.SequenceEqual(decisions.Order()))
            throw new InvalidOperationException("Reconciliation must account for each relevant new row exactly once.");
        await ValidatePreviousCoverageAsync(plan, rows, cancellationToken);
    }

    private async Task ValidatePreviousCoverageAsync(ImportReconciliation plan, IReadOnlyList<HistoricalImportRow> rows, CancellationToken cancellationToken)
    {
        var old = await ReadPreviousAsync(plan.PreviousSnapshotId, cancellationToken);
        if (old.Any(r => r[2].IsNull && r[3].IsNull))
            throw new InvalidOperationException("Resolve or explicitly exclude every relevant predecessor row before reconciling a changed snapshot.");
        var residual = old.Where(r => !r[2].IsNull && plan.RetainPreviousRows.Contains(checked((int)ReadLong(r[0])))).ToArray();
        if (residual.Length > 0)
            throw new InvalidOperationException($"Imported predecessor rows removed/changed: {string.Join(",", residual.Select(r => ReadLong(r[0])))}. Restore/reuse them or obtain separate approved data correction; retaining unexplained target expenses blocks cutover.");
        var accounted = plan.ReuseRows.Values.Concat(plan.RetainPreviousRows).ToArray();
        if (accounted.Length != accounted.Distinct().Count() || !old.Select(r => checked((int)ReadLong(r[0]))).Order().SequenceEqual(accounted.Order()))
            throw new InvalidOperationException("Every previous imported row must be reused once or explicitly retained; no implicit deletes or duplicate mapping.");
        foreach (var mapping in plan.ReuseRows)
        {
            var previous = old.Single(r => ReadLong(r[0]) == mapping.Value);
            if (previous[2].IsNull || !SameJson(previous[1].StringValue!, rows.Single(r => r.SourceRow == mapping.Key).RawJson))
                throw new InvalidOperationException("Changed record cannot reuse previous provenance; retain the old record explicitly and review the new row.");
        }
    }

    private async Task<List<IReadOnlyList<PostgresValue>>> ReadPreviousAsync(string id, CancellationToken cancellationToken)
    {
        var rows = new List<IReadOnlyList<PostgresValue>>();
        var cursor = 0L;
        while (true)
        {
            var page = await ExecuteAsync("SELECT source_row, CAST(raw_json AS text), expense_id, CAST(exclusion AS text) FROM expense_import_rows WHERE snapshot_id = :snapshot AND classification NOT IN ('Blank', 'NonExpense') AND source_row > :cursor ORDER BY source_row LIMIT 100",
                [Text("snapshot", id), Number("cursor", cursor)], cancellationToken);
            rows.AddRange(page.Rows);
            if (page.Rows.Count < 100) return rows;
            cursor = ReadLong(page.Rows[^1][0]);
        }
    }

    private async Task<IReadOnlyList<PostgresValue>> ReadRowAsync(string id, int row, CancellationToken cancellationToken) =>
        (await ExecuteAsync("SELECT CAST(raw_json AS text), expense_id, CAST(exclusion AS text) FROM expense_import_rows WHERE snapshot_id = :snapshot AND source_row = :row FOR UPDATE",
            [Text("snapshot", id), Number("row", row)], cancellationToken)).Rows.Single();

    private async Task<ExpenseSummary> ReadExpenseAsync(int id, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync("SELECT e.id, CAST(e.booking_date AS text), CAST(e.amount AS text), e.description, c.name, e.creator_email, CAST(e.create_date AS text), CAST(e.update_date AS text) FROM expenses e JOIN categories c ON c.id = e.category_id WHERE e.id = :expense",
            [Number("expense", id)], cancellationToken);
        var row = result.Rows.Single();
        var options = new JsonSerializerOptions { Converters = { new ExactDecimalJsonConverter() } };
        return new ExpenseSummary { Id = checked((int)ReadLong(row[0])), BookingDate = Instant(row[1]), Amount = JsonSerializer.Deserialize<decimal>(JsonSerializer.Serialize(row[2].StringValue), options),
            Description = row[3].StringValue, Category = row[4].StringValue, CreatorEmail = row[5].StringValue, CreationDate = Instant(row[6]), UpdateDate = Instant(row[7]), Currency = "EUR" };
    }

    private async Task<T> TransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await unit.BeginAsync(cancellationToken);
        try { var result = await action(); await unit.CommitAsync(cancellationToken); return result; }
        catch { try { await unit.RollbackAsync(CancellationToken.None); } catch { /* A lost commit acknowledgement is recovered by durable provenance on rerun. */ } throw; }
    }

    private Task<PostgresResult> LockAsync(string source, CancellationToken cancellationToken) => ExecuteAsync("SELECT 1 FROM pg_advisory_xact_lock(hashtextextended(:source, 0))", [Text("source", source)], cancellationToken);
    private async Task<string?> HeadAsync(string source, CancellationToken cancellationToken) => (await ExecuteAsync("SELECT current_snapshot_id FROM expense_import_sources WHERE source_key = :source", [Text("source", source)], cancellationToken)).Rows.FirstOrDefault()?[0].StringValue;
    private Task<PostgresResult> ExecuteAsync(string sql, IReadOnlyList<PostgresParameter> parameters, CancellationToken cancellationToken) => client.ExecuteAsync(sql, parameters, cancellationToken: cancellationToken);
    private static string SourceKey(HistoricalExpenseSnapshot snapshot) => JsonSerializer.Serialize(new[] { snapshot.SpreadsheetId, snapshot.Tab });
    private static PostgresParameter Text(string name, string value) => new(name, PostgresValue.FromString(value));
    private static PostgresParameter Number(string name, long value) => new(name, PostgresValue.FromLong(value));
    private static long ReadLong(PostgresValue value) => value.LongValue ?? long.Parse(value.StringValue!, CultureInfo.InvariantCulture);
    private static DateTimeOffset? Instant(PostgresValue value) => value.IsNull ? null : TimeService.InLisbon(DateTimeOffset.Parse(value.StringValue!, CultureInfo.InvariantCulture));
    private static bool SameJson(string first, string second) => JsonElement.DeepEquals(JsonDocument.Parse(first).RootElement, JsonDocument.Parse(second).RootElement);
    private static void ValidateApproval(string approver, string reason)
    { if (string.IsNullOrWhiteSpace(approver) || string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Explicit approver and reason are required."); }
}
