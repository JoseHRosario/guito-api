using GuitoApi.Infrastructure.Serialization;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuitoApi.DataTransferObjects.Input
{
    /// <summary>Expense input accepts any exactly representable decimal amount (ADR-0015).</summary>
    public class ExpenseCreate : IValidatableObject
    {
        [JsonRequired, JsonConverter(typeof(ExpenseDateJsonConverter))]
        public DateTimeOffset Date { get; set; }
        [Required, RegularExpression("EUR", ErrorMessage = "Only EUR is supported.")]
        public string Currency { get; set; } = "EUR";

        [JsonConverter(typeof(ExactDecimalJsonConverter))]
        public decimal Amount { get; set; }

        public string Description { get; set; } = "";
        public string Category { get; set; } = "";

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            // Reject only the removed timestamp field: ignoring it would silently discard time.
            if (AdditionalProperties?.Keys.Any(key => key.Equals("occurredAt", StringComparison.OrdinalIgnoreCase)) == true)
                yield return new ValidationResult("occurredAt is no longer accepted; supply the timestamp in date.", ["occurredAt"]);
        }
    }
}