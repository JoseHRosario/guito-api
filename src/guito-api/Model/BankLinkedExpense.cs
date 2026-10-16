namespace GuitoApi.Model;

public sealed record BankLinkedExpense(int Id, DateTimeOffset BookingDate, decimal Amount,
    string Currency, string Description, string Category);
