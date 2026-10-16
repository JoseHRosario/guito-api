using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace GuitoApi.DataTransferObjects.Input;

public sealed class BankExpenseLink
{
    [JsonRequired]
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    [Range(1, int.MaxValue)]
    public int ExpenseId { get; init; }
}
