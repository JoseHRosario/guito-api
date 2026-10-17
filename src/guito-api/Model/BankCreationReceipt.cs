namespace GuitoApi.Model;

public sealed record BankCreationReceipt(Guid OperationId, long TransactionId, int ExpenseId);
