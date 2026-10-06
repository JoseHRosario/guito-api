using GuitoApi.Configuration;
using GuitoApi.Infrastructure.Postgres;
using Microsoft.Extensions.Options;

namespace GuitoApi.Tests;

/// <summary>
/// Unit-of-work behavior over the Data API fake seam (issue #87): statements executed
/// while a unit is open join its transaction; commit/rollback close it; disposing an
/// open unit rolls back.
/// </summary>
public class DataApiUnitOfWorkTests
{
    private static (DataApiClient Client, FakeRdsDataServiceClient Fake, PostgresTransactionContext Context) BuildClient()
    {
        var fake = new FakeRdsDataServiceClient();
        var transactionContext = new PostgresTransactionContext();
        var client = new DataApiClient(
            fake,
            Options.Create(new DatabaseOptions
            {
                ResourceArn = "arn:aws:rds:eu-west-1:497087877832:cluster:db-cluster",
                SecretArn = "arn:aws:secretsmanager:eu-west-1:497087877832:secret:guito-api/db-dev-bbHadB",
                DatabaseName = "guito_dev",
            }),
            transactionContext);
        return (client, fake, transactionContext);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldJoinAmbientTransaction_AfterBegin()
    {
        var (client, fake, transactionContext) = BuildClient();
        fake.NextTransactionId = "tx-open";
        var unit = new DataApiUnitOfWork(client, transactionContext);

        await unit.BeginAsync();
        await client.ExecuteAsync("INSERT INTO t VALUES (1)");

        Assert.Equal("tx-open", fake.LastExecuteRequest?.TransactionId);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRunOutsideTransaction_AfterCommit()
    {
        var (client, fake, transactionContext) = BuildClient();
        fake.NextTransactionId = "tx-open";
        var unit = new DataApiUnitOfWork(client, transactionContext);

        await unit.BeginAsync();
        await unit.CommitAsync();
        await client.ExecuteAsync("INSERT INTO t VALUES (1)");

        Assert.Null(fake.LastExecuteRequest?.TransactionId);
        Assert.False(transactionContext.IsOpen);
        Assert.Equal("tx-open", fake.LastCommitRequest?.TransactionId);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRunOutsideTransaction_AfterRollback()
    {
        var (client, fake, transactionContext) = BuildClient();
        fake.NextTransactionId = "tx-open";
        var unit = new DataApiUnitOfWork(client, transactionContext);

        await unit.BeginAsync();
        await unit.RollbackAsync();
        await client.ExecuteAsync("INSERT INTO t VALUES (1)");

        Assert.Equal("tx-open", fake.LastRollbackRequest?.TransactionId);
        Assert.Null(fake.LastExecuteRequest?.TransactionId);
    }

    [Fact]
    public async Task DisposeAsync_ShouldRollBackAnOpenUnit()
    {
        var (client, fake, transactionContext) = BuildClient();
        fake.NextTransactionId = "tx-open";
        var unit = new DataApiUnitOfWork(client, transactionContext);

        await unit.BeginAsync();
        await unit.DisposeAsync();

        Assert.Equal("tx-open", fake.LastRollbackRequest?.TransactionId);
    }

    [Fact]
    public async Task BeginAsync_ShouldThrow_WhenUnitAlreadyOpen()
    {
        var (client, fake, transactionContext) = BuildClient();
        fake.NextTransactionId = "tx-open";
        var unit = new DataApiUnitOfWork(client, transactionContext);
        await unit.BeginAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.BeginAsync());
    }

    [Fact]
    public async Task CommitAsync_ShouldThrow_WhenNoUnitIsOpen()
    {
        var (client, fake, transactionContext) = BuildClient();
        var unit = new DataApiUnitOfWork(client, transactionContext);

        await Assert.ThrowsAsync<InvalidOperationException>(() => unit.CommitAsync());
    }
}