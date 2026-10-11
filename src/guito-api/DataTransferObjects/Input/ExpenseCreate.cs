using GuitoApi.Infrastructure.Serialization;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace GuitoApi.DataTransferObjects.Input
{
    /// <summary>Expense input accepts any exactly representable decimal amount (ADR-0015).</summary>
    public class ExpenseCreate
    {
        public DateTime Date { get; set; }
        public string? OccurredAt { get; set; }
        [Required, RegularExpression("EUR", ErrorMessage = "Only EUR is supported.")]
        public string Currency { get; set; } = "EUR";

        [JsonConverter(typeof(ExactDecimalJsonConverter))]
        public decimal Amount { get; set; }

        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
    }
}