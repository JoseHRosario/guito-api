using GuitoApi.Model;

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
        Task<string> CreateAsync(ExpenseDraft value, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists recent expenses. Postgres orders occurrence and ID descending;
        /// the compatibility Sheets adapter reads its configured tail slice.
        /// </summary>
        Task<IReadOnlyList<ExpenseSummary>> ListLatestAsync(int limit, CancellationToken cancellationToken = default);

        /// <summary>
        /// Cleans up only the Sheets smoke row with the given Id. Postgres does not
        /// support deletion; personal records cannot be deleted through this port.
        /// Throws <see cref="GuitoApi.Exceptions.ExpenseNotFoundException"/> when the smoke Id is absent or stale.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken cancellationToken = default);
    }
}
