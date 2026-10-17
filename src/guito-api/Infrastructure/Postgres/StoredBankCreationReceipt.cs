using GuitoApi.Model;

namespace GuitoApi.Infrastructure.Postgres;

internal sealed record StoredBankCreationReceipt(BankCreationReceipt Receipt, string Intent);
