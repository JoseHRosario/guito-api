using GuitoApi.Model;

namespace GuitoApi.Repositories;

/// <summary>
/// Authoritative category reader shared by API listing, AI extraction and bank suggestions.
/// Category maintenance is external to the API; requests never create categories.
/// </summary>
public interface ICategoriesRepository
{
    /// <summary>Lists the categories, ordered by name; empty when none exist.</summary>
    Task<IReadOnlyList<CategorySummary>> ListAsync(CancellationToken cancellationToken = default);
}
