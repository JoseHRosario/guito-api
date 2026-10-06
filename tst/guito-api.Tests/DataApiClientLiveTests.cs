using GuitoApi.Configuration;
using GuitoApi.Infrastructure.Postgres;
using Microsoft.Extensions.Options;

namespace GuitoApi.Tests;

/// <summary>
/// Live check for issue #87's acceptance criterion: a trivial SELECT 1 executed through
/// the DataApiClient wrapper against guito_dev. Skipped unless the caller opts in with
/// GUITO_DB_LIVE=1 AND AWS credentials are available — the default test run stays hermetic.
/// </summary>
public class DataApiClientLiveTests
{
    private const string LiveFlag = "GUITO_DB_LIVE";

    [Fact]
    public async Task SelectOne_ShouldReturn1_WhenExecutedAgainstGuitoDev()
    {
        if (Environment.GetEnvironmentVariable(LiveFlag) != "1")
            return; // hermetic default: no AWS access in CI or normal runs

        var options = Options.Create(new DatabaseOptions
        {
            ResourceArn = "arn:aws:rds:eu-west-1:497087877832:cluster:db-cluster",
            SecretArn = "arn:aws:secretsmanager:eu-west-1:497087877832:secret:guito-api/db-dev-bbHadB",
            DatabaseName = "guito_dev",
        });
        var client = new DataApiClient(new Amazon.RDSDataService.AmazonRDSDataServiceClient(), options, new PostgresTransactionContext());

        var result = await client.ExecuteAsync("SELECT 1 AS one");

        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0][0].LongValue);
    }

    [Fact]
    public async Task UnitOfWork_SelectOneInsideTransaction_ShouldCommit()
    {
        if (Environment.GetEnvironmentVariable(LiveFlag) != "1")
            return; // hermetic default: no AWS access in CI or normal runs

        var options = Options.Create(new DatabaseOptions
        {
            ResourceArn = "arn:aws:rds:eu-west-1:497087877832:cluster:db-cluster",
            SecretArn = "arn:aws:secretsmanager:eu-west-1:497087877832:secret:guito-api/db-dev-bbHadB",
            DatabaseName = "guito_dev",
        });
        var transactionContext = new PostgresTransactionContext();
        var client = new DataApiClient(new Amazon.RDSDataService.AmazonRDSDataServiceClient(), options, transactionContext);
        var unit = new DataApiUnitOfWork(client, transactionContext);

        await unit.BeginAsync();
        var result = await client.ExecuteAsync("SELECT 1 AS one"); // joins the open transaction
        await unit.CommitAsync();

        Assert.Single(result.Rows);
        Assert.Equal(1, result.Rows[0][0].LongValue);
    }
}