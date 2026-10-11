namespace GuitoApi.DataTransferObjects.Output;

public sealed record ExpenseHistoryDetail(int Id, DateTimeOffset BookingDate, DateTimeOffset CreationDate,
    DateTimeOffset? UpdateDate, string Amount, string Currency, string Description, string Category, string CreatorEmail);
