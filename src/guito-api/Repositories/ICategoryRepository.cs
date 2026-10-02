using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Repositories
{
    /// <summary>
    /// Persistence for the Category list. Implementations own every
    /// storage-specific detail (today: the Google Sheet's Config tab range,
    /// later Postgres); none of it may cross this boundary.
    /// </summary>
    public interface ICategoryRepository
    {
        /// <summary>Lists the categories, ordered by name. Empty when none exist.</summary>
        Task<IReadOnlyList<CategoryListDetail>> ListAsync(CancellationToken cancellationToken = default);
    }
}
