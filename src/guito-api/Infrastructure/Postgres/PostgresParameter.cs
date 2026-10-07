namespace GuitoApi.Infrastructure.Postgres;

/// <summary>A named SQL parameter bound to a PostgresValue (e.g. :booking_date).</summary>
public sealed record PostgresParameter(string Name, PostgresValue Value);