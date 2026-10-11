namespace GuitoApi.Model;

public sealed record ExpenseHistoryEntry(int Id, DateTimeOffset BookingDate, DateTimeOffset CreationDate,
    DateTimeOffset? UpdateDate, string Amount, string Currency, string Description, string Category, string CreatorEmail);
