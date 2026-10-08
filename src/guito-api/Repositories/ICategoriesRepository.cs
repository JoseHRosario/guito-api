using GuitoApi.Model;

namespace GuitoApi.Repositories;

/// <summary>
/// Read/upsert port of the Postgres categories mirror (issue #112, ADR-0014). NOT the
/// source of truth for categories — Sheets stays authoritative for writes (ADR-0005);
/// this mirror exists so bank-transaction suggestions can carry a real foreign key.
/// </summary>
public interface ICategoriesRepository
{
    /// <summary>Lists the categories, ordered by name. Empty when the seed script hasn't run.</summary>
    Task<IReadOnlyList<CategorySummary>> ListAsync(CancellationToken cancellationToken = default);
}
