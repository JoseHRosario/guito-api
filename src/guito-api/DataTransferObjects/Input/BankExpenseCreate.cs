using System.Text.Json.Serialization;

namespace GuitoApi.DataTransferObjects.Input;

public sealed class BankExpenseCreate : ExpenseCreate
{
    [JsonRequired]
    public Guid OperationId { get; set; }
}
