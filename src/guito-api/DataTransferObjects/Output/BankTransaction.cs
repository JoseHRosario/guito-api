using System.Text.Json.Serialization;
using GuitoApi.Infrastructure.Serialization;

namespace GuitoApi.DataTransferObjects.Output;

/// <summary>Bank review wire shape for list and reversible review actions (#133).</summary>
public sealed record BankTransaction(
    long Id,
    string AccountUid,
    DateOnly BookingDate,
    [property: JsonConverter(typeof(ExactDecimalJsonConverter))] decimal Amount,
    string Currency,
    string? RemittanceInformation,
    SuggestedCategory? SuggestedCategory,
    string Status,
    string ReviewStatus,
    BankTransactionLinkedExpense? LinkedExpense,
    int SuggestedExpenseCount = 0);
