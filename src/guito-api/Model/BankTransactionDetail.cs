namespace GuitoApi.Model;

/// <summary>A stored Bank Transaction and its separate review state, suggestion and optional Expense link.</summary>
public sealed record BankTransactionDetail(
    long Id,
    string AccountUid,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    string? RemittanceInformation,
    BankSuggestedCategory? SuggestedCategory = null,
    string Status = "BOOK",
    string ReviewStatus = "needsReview",
    BankLinkedExpense? LinkedExpense = null,
    int SuggestedExpenseCount = 0);
