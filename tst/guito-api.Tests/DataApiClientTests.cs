using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using GuitoApi.Configuration;
using GuitoApi.Infrastructure.Postgres;
using Microsoft.Extensions.Options;

namespace GuitoApi.Tests;

/// <summary>
/// Unit tests for DataApiClient — the RDS Data API wrapper (issue #87). The SDK client
/// is overridden in-memory (FakeRdsDataServiceClient), so no AWS access is needed.
/// </summary>
public class DataApiClientTests
{
    private static DatabaseOptions Options() => new()
    {
        ResourceArn = "arn:aws:rds:eu-west-1:497087877832:cluster:db-cluster",
        SecretArn = "arn:aws:secretsmanager:eu-west-1:497087877832:secret:guito-api/db-dev-bbHadB",
        DatabaseName = "guito_dev",
    };

    [Fact]
    public async Task ExecuteAsync_ShouldMapStringAndLongFields_IntoResult()
    {
        var fake = new FakeRdsDataServiceClient();
        fake.NextExecuteResponse = new ExecuteStatementResponse
        {
            ColumnMetadata = [new ColumnMetadata { Name = "name" }, new ColumnMetadata { Name = "n" }],
            Records =
            [
                [new Field { StringValue = "alpha" }, new Field { LongValue = 1 }],
                [new Field { StringValue = "beta" }, new Field { LongValue = 2 }],
            ],
        };
        var client = new DataApiClient(fake, Microsoft.Extensions.Options.Options.Create(Options()), new PostgresTransactionContext());

        var result = await client.ExecuteAsync("SELECT name, n FROM t");

        Assert.Equal("SELECT name, n FROM t", fake.LastExecuteRequest?.Sql);
        Assert.Equal(["name", "n"], result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("alpha", result.Rows[0][0].StringValue);
        Assert.True(result.Rows[0][1].LongValue == 1);
        Assert.Equal("beta", result.Rows[1][0].StringValue);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMapNullAndNumericAndBoolFields()
    {
        var fake = new FakeRdsDataServiceClient();
        fake.NextExecuteResponse = new ExecuteStatementResponse
        {
            ColumnMetadata = [new ColumnMetadata { Name = "a" }, new ColumnMetadata { Name = "b" }, new ColumnMetadata { Name = "c" }],
            Records =
            [
                [new Field { IsNull = true }, new Field { DoubleValue = 12.5 }, new Field { BooleanValue = true }],
            ],
        };
        var client = new DataApiClient(fake, Microsoft.Extensions.Options.Options.Create(Options()), new PostgresTransactionContext());

        var result = await client.ExecuteAsync("SELECT a, b, c FROM t");

        Assert.True(result.Rows[0][0].IsNull);
        Assert.Equal(12.5, result.Rows[0][1].DoubleValue);
        Assert.True(result.Rows[0][2].BooleanValue == true);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCarryParametersAndTransactionId()
    {
        var fake = new FakeRdsDataServiceClient();
        fake.NextExecuteResponse = new ExecuteStatementResponse { NumberOfRecordsUpdated = 1 };
        var client = new DataApiClient(fake, Microsoft.Extensions.Options.Options.Create(Options()), new PostgresTransactionContext());

        await client.ExecuteAsync(
            "INSERT INTO t VALUES (:v)",
            [new PostgresParameter("v", PostgresValue.FromString("x"))],
            transactionId: "tx-1");

        var request = fake.LastExecuteRequest!;
        var parameter = Assert.Single(request.Parameters);
        Assert.Equal("v", parameter.Name);
        Assert.Equal("x", parameter.Value.StringValue);
        Assert.Equal("tx-1", request.TransactionId);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnNumberOfRecordsUpdated_WhenNoResultSet()
    {
        var fake = new FakeRdsDataServiceClient();
        fake.NextExecuteResponse = new ExecuteStatementResponse { NumberOfRecordsUpdated = 3 };
        var client = new DataApiClient(fake, Microsoft.Extensions.Options.Options.Create(Options()), new PostgresTransactionContext());

        var result = await client.ExecuteAsync("DELETE FROM t");

        Assert.Empty(result.Rows);
        Assert.Equal(3, result.NumberOfRecordsUpdated);
    }

    [Fact]
    public async Task Transactions_ShouldFlowThroughToTheSdkClient()
    {
        var fake = new FakeRdsDataServiceClient();
        fake.NextTransactionId = "tx-9";
        fake.NextCommitResponse = new CommitTransactionResponse { TransactionStatus = "Transaction Committed" };
        var client = new DataApiClient(fake, Microsoft.Extensions.Options.Options.Create(Options()), new PostgresTransactionContext());

        var txId = await client.BeginTransactionAsync();
        await client.CommitAsync(txId);
        await client.RollbackAsync("tx-other");

        Assert.Equal("tx-9", txId);
        Assert.Equal("tx-9", fake.LastCommitRequest?.TransactionId);
        Assert.Equal("tx-other", fake.LastRollbackRequest?.TransactionId);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSurfaceSdkExceptions_ToCaller()
    {
        var fake = new FakeRdsDataServiceClient();
        fake.ExecuteException = new AmazonRDSDataServiceException("boom");
        var client = new DataApiClient(fake, Microsoft.Extensions.Options.Options.Create(Options()), new PostgresTransactionContext());

        await Assert.ThrowsAsync<AmazonRDSDataServiceException>(() => client.ExecuteAsync("SELECT 1"));
    }
}