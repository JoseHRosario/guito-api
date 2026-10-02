using GuitoApi.DataTransferObjects.Input;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Exceptions;
using GuitoApi.Repositories;

namespace GuitoApi.Services.ArtificialIntelligence
{
    /// <summary>
    /// Thin adapter over the expense-extraction repository (issue #69, ADR 0011): applies
    /// the business rules — prompt required, note must read as an expense, amount positive
    /// (ADR 0010) — and assembles the wire response. All OpenRouter provider detail lives
    /// in Infrastructure/AI.
    /// </summary>
    public class ExtractMethodService(
        IExpenseExtractionRepository expenseExtractionRepository,
        ICategoryRepository categoryRepository) : IExtractMethodService
    {
        public async Task<ExpenseExtracted> ExtractMethodAsync(ExpenseExtract input,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(input.Prompt))
                throw new ProblemException(400, "prompt is required");

            // Jev gate first: reject non-expense notes before spending a chat-model call.
            if (!await expenseExtractionRepository.IsExpenseNoteAsync(input.Prompt, cancellationToken))
                throw new ProblemException(400, "The prompt does not look like an expense note");

            var extracted = await expenseExtractionRepository.ExtractAsync(input.Prompt, cancellationToken);
            var amount = NormalizePositiveAmount(extracted.Amount);

            var categories = await categoryRepository.ListAsync(cancellationToken);
            var category = await expenseExtractionRepository.ResolveCategoryAsync(
                input.Prompt, extracted.Description, categories, cancellationToken);

            return new ExpenseExtracted
            {
                Date = extracted.Date,
                Amount = amount,
                Description = extracted.Description,
                Category = category,
            };
        }

        private static decimal NormalizePositiveAmount(decimal amount)
        {
            var positive = Math.Abs(amount);
            if (positive <= 0)
                throw new ProblemException(400, "Could not extract a positive amount from the prompt");
            return positive;
        }
    }
}