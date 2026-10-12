namespace GuitoApi.DataTransferObjects.Output;

public sealed record ExpenseHistoryDetail(int Id, string Amount, string Currency,
    DateTimeOffset BookingDate, string? Description, string? Category, string? CreatorEmail,
    DateTimeOffset? CreationDate, DateTimeOffset? UpdateDate);
