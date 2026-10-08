using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Tests;

/// <summary>
/// Hermetic fake of ICategoriesRepository (issue #112): the categories are scripted —
/// in production the table is seeded by the migration script (004), not by the app.
/// </summary>
public class FakeCategoriesRepository : ICategoriesRepository
{
    /// <summary>Scripted category rows, exactly what a seeded table would return.</summary>
    public List<CategorySummary> Categories { get; } = [];

    public Task<IReadOnlyList<CategorySummary>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CategorySummary>>([.. Categories]);
}
