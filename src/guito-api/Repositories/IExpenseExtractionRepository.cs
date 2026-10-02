using GuitoApi.DataTransferObjects.Output;

namespace GuitoApi.Repositories
{
    /// <summary>
    /// Persistence-extraction boundary for AI expense extraction (issue #69): the
    /// implementation owns every provider-specific detail (today: OpenRouter chat
    /// + Jev Decisions calls); none of it may cross into Services.
    /// </summary>
    public interface IExpenseExtractionRepository
    {
        /// <summary>Jev noul gate: whether the note reads as an expense record.</summary>
        Task<bool> IsExpenseNoteAsync(string prompt, CancellationToken cancellationToken = default);

        /// <summary>Extracts the raw date/amount/merchant fields from the note.</summary>
        Task<ExpenseExtractionResult> ExtractAsync(string prompt, CancellationToken cancellationToken = default);

        /// <summary>Picks a category among the given candidates; empty string when none fit.</summary>
        Task<string> ResolveCategoryAsync(string prompt, string description,
            IReadOnlyList<CategoryListDetail> categories, CancellationToken cancellationToken = default);
    }
}