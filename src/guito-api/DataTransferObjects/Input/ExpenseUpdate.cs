using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using GuitoApi.Infrastructure.Serialization;

namespace GuitoApi.DataTransferObjects.Input;

public sealed class ExpenseUpdate
{
    [JsonRequired, Required(AllowEmptyStrings = true)]
    public string Description { get; set; } = "";
    [JsonRequired, Required]
    public string Category { get; set; } = "";
    [JsonRequired, JsonConverter(typeof(ExactDecimalJsonConverter))]
    public decimal Amount { get; set; }
    [JsonRequired, JsonConverter(typeof(ExpenseDateJsonConverter))]
    public DateTimeOffset BookingDate { get; set; }
    [Required, RegularExpression("EUR", ErrorMessage = "Only EUR is supported.")]
    public string Currency { get; set; } = "EUR";
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
