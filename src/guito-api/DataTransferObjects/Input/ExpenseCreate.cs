using System.ComponentModel.DataAnnotations;

namespace GuitoApi.DataTransferObjects.Input
{
    /// <summary>
    /// Amounts are stored positive (ADR 0010): the outflow is implied by the
    /// record being an Expense. The API rejects non-positive amounts so no
    /// caller can persist a negative (the UI's earlier negation is removed;
    /// no data migration was needed — the sheet was already positive).
    /// </summary>
    public class ExpenseCreate
    {
        public DateTime Date { get; set; }

        [Range(typeof(decimal), "0.01", "79228162514264337593543950335",
            ErrorMessage = "Amount must be a positive number.")]
        public decimal Amount { get; set; }

        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
    }
}