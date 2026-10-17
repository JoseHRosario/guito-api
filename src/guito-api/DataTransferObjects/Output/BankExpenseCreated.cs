namespace GuitoApi.DataTransferObjects.Output;

public sealed record BankExpenseCreated(string OperationId, long TransactionId, int ExpenseId);
