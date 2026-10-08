using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using GuitoApi.Configuration;
using Microsoft.Extensions.Options;

namespace GuitoApi.Infrastructure.Postgres;

/// <summary>
/// IPostgresDataApiClient over the RDS Data API SDK (issue #87, ADR-0013). Raw SQL in,
/// mapped PostgresValue rows out — no EF/Dapper. One HTTPS call per statement; the Lambda
/// stays VPC-less. SDK exceptions surface to the caller (translation belongs to the layer
/// that knows the operation, starting with the repository in #88). Exception: a fresh
/// DatabaseResumingException is retried — Aurora Serverless v2 auto-pauses at min ACU and
/// rejects the first statements while resuming (hit live in staging, issue #91).
/// </summary>
public class DataApiClient : IPostgresDataApiClient
{
    private static readonly TimeSpan[] ResumeRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];

    private readonly IAmazonRDSDataService _sdk;
    private readonly DatabaseOptions _options;
    private readonly PostgresTransactionContext _transactionContext;
    private readonly TimeSpan[] _resumeRetryDelays;

    public DataApiClient(
        IAmazonRDSDataService sdk,
        IOptions<DatabaseOptions> options,
        PostgresTransactionContext transactionContext,
        TimeSpan[]? resumeRetryDelays = null)
    {
        _sdk = sdk;
        _options = options.Value;
        _transactionContext = transactionContext;
        _resumeRetryDelays = resumeRetryDelays ?? ResumeRetryDelays;
    }

    public async Task<PostgresResult> ExecuteAsync(
        string sql,
        IReadOnlyList<PostgresParameter>? parameters = null,
        string? transactionId = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ExecuteStatementRequest
        {
            ResourceArn = _options.ResourceArn,
            SecretArn = _options.SecretArn,
            Database = _options.DatabaseName,
            Sql = sql,
            // Explicit id wins; otherwise join the ambient unit of work, if one is open.
            TransactionId = transactionId ?? _transactionContext.CurrentTransactionId,
            Parameters = parameters?.Select(p => new SqlParameter
            {
                Name = p.Name,
                Value = ToSdkField(p.Value),
            }).ToList(),
            ContinueAfterTimeout = true,
        };

        var attempt = 0;
        while (true)
        {
            try
            {
                var response = await _sdk.ExecuteStatementAsync(request, cancellationToken);
                return FromSdk(response);
            }
            catch (DatabaseResumingException) when (attempt < _resumeRetryDelays.Length)
            {
                // Aurora resumes in a few seconds; a fresh error class (or a still-paused
                // cluster past the retries) surfaces to the caller unchanged.
                await Task.Delay(_resumeRetryDelays[attempt], cancellationToken);
                attempt++;
            }
        }
    }

    public async Task<string> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                var response = await _sdk.BeginTransactionAsync(new BeginTransactionRequest
                {
                    ResourceArn = _options.ResourceArn,
                    SecretArn = _options.SecretArn,
                    Database = _options.DatabaseName,
                }, cancellationToken);
                return response.TransactionId;
            }
            catch (DatabaseResumingException) when (attempt < _resumeRetryDelays.Length)
            {
                await Task.Delay(_resumeRetryDelays[attempt], cancellationToken);
                attempt++;
            }
        }
    }

    public Task CommitAsync(string transactionId, CancellationToken cancellationToken = default) =>
        _sdk.CommitTransactionAsync(new CommitTransactionRequest
        {
            ResourceArn = _options.ResourceArn,
            SecretArn = _options.SecretArn,
            TransactionId = transactionId,
        }, cancellationToken);

    public Task RollbackAsync(string transactionId, CancellationToken cancellationToken = default) =>
        _sdk.RollbackTransactionAsync(new RollbackTransactionRequest
        {
            ResourceArn = _options.ResourceArn,
            SecretArn = _options.SecretArn,
            TransactionId = transactionId,
        }, cancellationToken);

    private static Field ToSdkField(PostgresValue value)
    {
        var field = new Field();
        if (value.IsNull)
            field.IsNull = true;
        else if (value.StringValue is not null)
            field.StringValue = value.StringValue;
        else if (value.LongValue is not null)
            field.LongValue = value.LongValue.Value;
        else if (value.DoubleValue is not null)
            field.DoubleValue = value.DoubleValue.Value;
        else if (value.BooleanValue is not null)
            field.BooleanValue = value.BooleanValue.Value;
        return field;
    }

    private static PostgresResult FromSdk(ExecuteStatementResponse response)
    {
        var columns = (response.ColumnMetadata ?? []).Select(c => c.Name).ToList();
        var rows = (response.Records ?? [])
            .Select(record => record.Select(FromSdkField).ToList())
            .ToList();
        return new PostgresResult(columns, rows, response.NumberOfRecordsUpdated ?? 0);
    }

    private static PostgresValue FromSdkField(Field field) =>
        field.IsNull == true ? PostgresValue.Null()
        : field.StringValue is not null ? PostgresValue.FromString(field.StringValue)
        : field.LongValue is not null ? PostgresValue.FromLong(field.LongValue.Value)
        : field.DoubleValue is not null ? PostgresValue.FromDouble(field.DoubleValue.Value)
        : field.BooleanValue is not null ? PostgresValue.FromBoolean(field.BooleanValue.Value)
        : PostgresValue.Null();
}