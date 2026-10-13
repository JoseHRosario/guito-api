using System.Text.Json.Serialization;
using GuitoApi.Infrastructure.Serialization;

namespace GuitoApi.DataTransferObjects.Output;

public sealed record BankTransactionLinkedExpense(int Id, DateTimeOffset BookingDate,
    [property: JsonConverter(typeof(ExactDecimalJsonConverter))] decimal Amount,
    string Currency, string Description, string Category);
