using GuitoApi.Infrastructure.Serialization;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GuitoApi.DataTransferObjects.Input
{
    /// <summary>Expense input accepts any exactly representable decimal amount (ADR-0015).</summary>
    public class ExpenseCreate
    {
        [JsonRequired, JsonConverter(typeof(ExpenseDateJsonConverter))]
        public DateTimeOffset BookingDate { get; set; }
        [Required, RegularExpression("EUR", ErrorMessage = "Only EUR is supported.")]
        public string Currency { get; set; } = "EUR";

        [JsonConverter(typeof(ExactDecimalJsonConverter))]
        public decimal Amount { get; set; }

        public string Description { get; set; } = "";
        public string Category { get; set; } = "";

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    }
}