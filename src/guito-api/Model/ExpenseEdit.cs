namespace GuitoApi.Model;

public sealed record ExpenseEdit(string Description, string Category, decimal Amount, DateTimeOffset BookingDate);
