namespace GuitoApi.Model;

/// <summary>
/// One row of the Postgres categories mirror (issue #112, ADR-0014): seeded from the
/// Sheets category list; Sheets remains the source of truth for category writes.
/// </summary>
public sealed record CategorySummary(long Id, string Name, string? Description);
