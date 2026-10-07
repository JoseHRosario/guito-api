namespace GuitoApi.Infrastructure.Postgres;

/// <summary>
/// A single SQL value crossing the Data API boundary (issue #87). Read path: maps an
/// SDK Field into a PostgresValue; write path: a PostgresValue maps into a Field.
/// Null-safe by construction — IsNull is authoritative for nullable columns.
/// </summary>
public sealed record PostgresValue(
    bool IsNull,
    string? StringValue,
    long? LongValue,
    double? DoubleValue,
    bool? BooleanValue)
{
    public static PostgresValue Null() => new(true, null, null, null, null);
    public static PostgresValue FromString(string value) => new(false, value, null, null, null);
    public static PostgresValue FromLong(long value) => new(false, null, value, null, null);
    public static PostgresValue FromDouble(double value) => new(false, null, null, value, null);
    public static PostgresValue FromBoolean(bool value) => new(false, null, null, null, value);
}