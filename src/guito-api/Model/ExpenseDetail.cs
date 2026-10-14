namespace GuitoApi.Model;

public sealed record ExpenseDetail(int Id, string Amount, string Description, string Category,
    DateTimeOffset BookingDate, string CreatorEmail, DateTimeOffset CreationDate,
    DateTimeOffset? UpdateDate, string Revision);
