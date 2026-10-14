using GuitoApi.Model;

namespace GuitoApi.Repositories
{
    /// <summary>
    /// Persistence for the Expense aggregate. Implementations own every
    /// storage-specific detail. The Id is an immutable Int32 database identity;
    /// callers must not interpret it.
    /// </summary>
    public interface IExpenseRepository
    {
        /// <summary>Persists the expense and returns its integer Id.</summary>
        Task<int> CreateAsync(ExpenseDraft value, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists recent expenses ordered by occurrence and ID descending.
        /// </summary>
        Task<IReadOnlyList<ExpenseSummary>> ListLatestAsync(int limit, CancellationToken cancellationToken = default);

        /// <summary>Reads personal Expense detail including its opaque revision.</summary>
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
