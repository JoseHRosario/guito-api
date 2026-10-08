using GuitoApi.Model;
using GuitoApi.Infrastructure.Postgres;
using GuitoApi.Repositories;

namespace GuitoApi.Tests;

/// <summary>
/// Direct unit tests for DataApiPostgresBankTransactionRepository (issue #88) over a
/// fake IPostgresDataApiClient: insert-or-skip idempotency by sync_key, pending list
/// filtering and mapping.
/// </summary>
public class DataApiPostgresBankTransactionRepositoryTests
{
    private static BankTransactionInsert BuildInsert(string? remittanceInformation = "Padaria") => new()
    {
        AccountUid = "eb-account-1",
        BookingDate = new DateOnly(2026, 10, 4),
        Amount = 2.30m,
        Currency = "EUR",
        Direction = "DBIT",
        RemittanceInformation = remittanceInformation,
        Status = "BOOK",
        SyncKey = "sync-key-1",
    };

    [Fact]
    public async Task InsertOrSkipAsync_ShouldReturnTrue_AndExecuteInsert_WhenRowIsNew()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(
            new PostgresResult(["id"], [[PostgresValue.FromLong(1)]], 1));
        var repository = new DataApiPostgresBankTransactionRepository(client);

        var isNew = await repository.InsertOrSkipAsync(BuildInsert());

        Assert.True(isNew);
        var sql = Assert.Single(client.ExecutedSql);
        Assert.Contains("INSERT INTO bank_transactions", sql);
        Assert.Contains("ON CONFLICT (sync_key) DO NOTHING", sql);
        Assert.Contains("RETURNING id", sql);
    }

    [Fact]
    public async Task InsertOrSkipAsync_ShouldReturnFalse_WhenSyncKeyConflicts()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult([], [], 0));
        var repository = new DataApiPostgresBankTransactionRepository(client);

        var isNew = await repository.InsertOrSkipAsync(BuildInsert());

        Assert.False(isNew);
    }

    [Fact]
    public async Task InsertOrSkipAsync_ShouldBindEveryColumn_AsNamedParameter()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult([], [], 0));
        var repository = new DataApiPostgresBankTransactionRepository(client);

        await repository.InsertOrSkipAsync(BuildInsert());

        var parameters = Assert.Single(client.ExecutedParameters)!;
        var byName = parameters.ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(8, parameters.Count);
        Assert.Equal("eb-account-1", byName["account_uid"].StringValue);
        Assert.Equal("2026-10-04", byName["booking_date"].StringValue);
        Assert.True(byName["amount"].DoubleValue == 2.30);
        Assert.Equal("EUR", byName["currency"].StringValue);
        Assert.Equal("DBIT", byName["direction"].StringValue);
        Assert.Equal("Padaria", byName["remittance_information"].StringValue);
        Assert.Equal("BOOK", byName["status"].StringValue);
        Assert.Equal("sync-key-1", byName["sync_key"].StringValue);
    }

    [Fact]
    public async Task InsertOrSkipAsync_ShouldBindNullRemittance_WhenItIsAbsent()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult([], [], 0));
        var repository = new DataApiPostgresBankTransactionRepository(client);

        await repository.InsertOrSkipAsync(BuildInsert(remittanceInformation: null));

        var parameters = Assert.Single(client.ExecutedParameters)!;
        Assert.True(parameters.Single(p => p.Name == "remittance_information").Value.IsNull);
    }

    [Fact]
    public async Task InsertOrSkipAsync_ShouldForwardCancellationToken()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult([], [], 0));
        var repository = new DataApiPostgresBankTransactionRepository(client);
        using var cancellationSource = new CancellationTokenSource();

        await repository.InsertOrSkipAsync(BuildInsert(), cancellationSource.Token);

        Assert.Equal(cancellationSource.Token, Assert.Single(client.ExecutedCancellationTokens));
    }

    [Fact]
    public async Task ListPendingAsync_ShouldQueryUnmatchedRows_NewestFirst()
    {
        var client = new FakePostgresDataApiClient();
        var repository = new DataApiPostgresBankTransactionRepository(client);

        await repository.ListPendingAsync();

        var sql = Assert.Single(client.ExecutedSql);
        Assert.Contains("FROM bank_transactions", sql);
        Assert.Contains("WHERE expense_id IS NULL", sql);
        Assert.Contains("ORDER BY booking_date DESC, id DESC", sql);
    }

    [Fact]
    public async Task ListPendingAsync_ShouldMapRows_WhenPendingTransactionsExist()
    {
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult(
            ["id", "account_uid", "booking_date", "amount", "currency", "remittance_information"],
            [
                [PostgresValue.FromLong(7), PostgresValue.FromString("eb-account-1"),
                    PostgresValue.FromString("2026-10-05"), PostgresValue.FromDouble(9.20),
                    PostgresValue.FromString("EUR"), PostgresValue.FromString("Quicksilver")],
                [PostgresValue.FromLong(6), PostgresValue.FromString("eb-account-1"),
                    PostgresValue.FromString("2026-10-04"), PostgresValue.FromDouble(3.60),
                    PostgresValue.FromString("EUR"), PostgresValue.Null()],
            ],
            0));
        var repository = new DataApiPostgresBankTransactionRepository(client);

        var pending = await repository.ListPendingAsync();

        Assert.Equal(2, pending.Count);
        Assert.Equal(7, pending[0].Id);
        Assert.Equal("eb-account-1", pending[0].AccountUid);
        Assert.Equal(new DateOnly(2026, 10, 5), pending[0].BookingDate);
        Assert.Equal(9.20m, pending[0].Amount);
        Assert.Equal("EUR", pending[0].Currency);
        Assert.Equal("Quicksilver", pending[0].RemittanceInformation);
        Assert.Equal(6, pending[1].Id);
        Assert.Null(pending[1].RemittanceInformation);
    }

    [Fact]
    public async Task ListPendingAsync_ShouldReturnEmpty_WhenNoPendingRowsExist()
    {
        var client = new FakePostgresDataApiClient();
        var repository = new DataApiPostgresBankTransactionRepository(client);

        var pending = await repository.ListPendingAsync();

        Assert.Empty(pending);
    }

    [Fact]
    public async Task ListPendingAsync_ShouldMapStringEncodedNumerics_WhenDataApiReturnsStringFields()
    {
        // The live Data API returns bigint/numeric columns as string fields (seen in
        // staging, issue #91): id and amount arrive as strings, not typed values.
        var client = new FakePostgresDataApiClient();
        client.NextResults.Enqueue(new PostgresResult(
            ["id", "account_uid", "booking_date", "amount", "currency", "remittance_information"],
            [
                [PostgresValue.FromString("7"), PostgresValue.FromString("eb-account-1"),
                    PostgresValue.FromString("2026-10-05"), PostgresValue.FromString("9.20"),
                    PostgresValue.FromString("EUR"), PostgresValue.FromString("Quicksilver")],
                [PostgresValue.FromString("6"), PostgresValue.FromString("eb-account-1"),
                    PostgresValue.FromString("2026-10-04"), PostgresValue.FromString("3.60"),
                    PostgresValue.FromString("EUR"), PostgresValue.Null()],
            ],
            0));
        var repository = new DataApiPostgresBankTransactionRepository(client);

        var pending = await repository.ListPendingAsync();

        Assert.Equal(2, pending.Count);
        Assert.Equal(7, pending[0].Id);
        Assert.Equal(9.20m, pending[0].Amount);
        Assert.Equal(new DateOnly(2026, 10, 5), pending[0].BookingDate);
        Assert.Equal(6, pending[1].Id);
        Assert.Equal(3.60m, pending[1].Amount);
        Assert.Null(pending[1].RemittanceInformation);
    }
}
