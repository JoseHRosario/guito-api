using System.ComponentModel.DataAnnotations;

namespace GuitoApi.DataTransferObjects.Input;

public sealed class ExpenseHistoryQuery
{
    public int PageSize { get; set; } = 100;
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Cursor { get; set; }
}
