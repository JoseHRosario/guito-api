using GuitoApi.Model;
using GuitoApi.Repositories;

namespace GuitoApi.Tests;

/// <summary>
/// Hermetic fake of IBankAccountRepository (issue #89): canned linked accounts and
/// recorded upserts, standing in for the Data API-backed repository.
/// </summary>
public class FakeBankAccountRepository : IBankAccountRepository
{
    public List<BankAccountSummary> Accounts { get; } = [];
    public List<BankAccountUpsert> Upserts { get; } = [];

    public Task<IReadOnlyList<BankAccountSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BankAccountSummary>>(Accounts.ToList());

    public Task UpsertAsync(BankAccountUpsert account, CancellationToken cancellationToken = default)
    {
        Upserts.Add(account);
        return Task.CompletedTask;
    }
}