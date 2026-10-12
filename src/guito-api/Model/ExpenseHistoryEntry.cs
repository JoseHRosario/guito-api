namespace GuitoApi.Model;

public sealed record ExpenseHistoryEntry(int Id, string Amount, DateTimeOffset BookingDate,
    string? Description, string? Category, string? CreatorEmail,
    DateTimeOffset? CreationDate, DateTimeOffset? UpdateDate);
