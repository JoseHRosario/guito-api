using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Repositories
{
    /// <summary>
    /// Persistence for the Expense aggregate. Implementations own every
    /// storage-specific detail and it must never cross this boundary: today the
    /// Google Sheet (ranges, the Smoke-scope header resolution, the
    /// row-index↔Id mapping, the Year/Month formula backfill), later Postgres.
    /// The Id is opaque — today the sheet row index, later a database key
    /// (CONTEXT.md); callers must not interpret it.
    /// </summary>
    public interface IExpenseRepository
    {
        /// <summary>Persists the expense and returns its opaque Id.</summary>
        Task<string> CreateAsync(ExpenseCreate value, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists the latest expenses in stored order (oldest first, matching the
        /// datastore's read direction). Empty when the datastore holds none.
        /// </summary>
        Task<IReadOnlyList<ExpenseListLatestDetail>> ListLatestAsync(int limit, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes the expense with the given Id. Throws
        /// <see cref="ExpenseNotFoundException"/> when the Id is absent or stale.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    }
}
