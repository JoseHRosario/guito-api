namespace GuitoApi.Infrastructure.Postgres;

/// <summary>
/// The outcome of a Data API statement: column names for a result set, the rows as
/// PostgresValue cells, and NumberOfRecordsUpdated for writes (0 for SELECTs).
/// </summary>
public sealed record PostgresResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<PostgresValue>> Rows,
    long NumberOfRecordsUpdated);