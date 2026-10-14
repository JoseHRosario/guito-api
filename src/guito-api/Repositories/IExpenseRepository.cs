using GuitoApi.Model;

namespace GuitoApi.Repositories
{
    /// <summary>
    /// Persistence for the Expense aggregate. Implementations own every
    /// storage-specific detail; Postgres personal operations and compatibility
    /// Sheets smoke cleanup remain distinct.
    /// The Id is an Int32 identity — a Sheets absolute row index or a Postgres primary key
    /// (CONTEXT.md); callers must not interpret it.
    /// </summary>
    public interface IExpenseRepository
    {
        /// <summary>Persists the expense and returns its integer Id.</summary>
        Task<int> CreateAsync(ExpenseDraft value, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists recent expenses. Postgres orders occurrence and ID descending;
        /// the compatibility Sheets adapter reads its configured tail slice.
        /// </summary>
        Task<IReadOnlyList<ExpenseSummary>> ListLatestAsync(int limit, CancellationToken cancellationToken = default);

        /// <summary>
        /// Cleans up only the Sheets smoke row with the given Id. Postgres rejects
        /// this cleanup; personal deletion requires the separate revision-aware operation.
        /// Throws <see cref="GuitoApi.Exceptions.ExpenseNotFoundException"/> when the smoke Id is absent or stale.
        /// </summary>
        Task DeleteSmokeAsync(int id, CancellationToken cancellationToken = default);

        /// <summary>Reads personal Postgres detail including its opaque revision; unavailable stores/scopes reject with 409.</summary>
        Task<ExpenseDetail> ReadAsync(int id, CancellationToken cancellationToken = default);

        /// <summary>Replaces editable personal fields atomically against the loaded revision.</summary>
        Task<ExpenseDetail> UpdateAsync(int id, long revision, ExpenseEdit value, CancellationToken cancellationToken = default);

        /// <summary>Deletes the personal Expense and unlinks bank records atomically against the loaded revision.</summary>
        Task DeleteAsync(int id, long revision, CancellationToken cancellationToken = default);

        /// <summary>Returns every exact-category expense in the inclusive/exclusive booking interval, or fails.
        /// A null endExclusiveOrStartOfYear10000 denotes the start of year 10000, beyond DateTimeOffset's range.</summary>
        Task<IReadOnlyList<ExpenseSummary>> ListAsync(string category, DateTimeOffset start,
            DateTimeOffset? endExclusiveOrStartOfYear10000, CancellationToken cancellationToken = default);
    }
}
