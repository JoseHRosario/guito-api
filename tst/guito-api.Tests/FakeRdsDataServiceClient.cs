using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;

namespace GuitoApi.Tests;

/// <summary>
/// In-memory override of the RDS Data API SDK client for DataApiClientTests (issue #87).
/// Subclasses the real client and overrides the four virtual operations, recording the
/// requests and returning scripted responses — no AWS access.
/// </summary>
public class FakeRdsDataServiceClient : AmazonRDSDataServiceClient
{
    // The base client's constructor validates region/credentials even though the fake
    // overrides every operation — CI runners have no ambient AWS config, so pin explicit
    // dummy values to keep construction hermetic.
    private static AmazonRDSDataServiceConfig HermeticConfig() => new()
    {
        RegionEndpoint = Amazon.RegionEndpoint.EUWest1,
    };

    public FakeRdsDataServiceClient()
        : base(new Amazon.Runtime.BasicAWSCredentials("hermetic", "hermetic"), HermeticConfig())
    {
    }
    public ExecuteStatementResponse? NextExecuteResponse { get; set; } = new();
    public string? NextTransactionId { get; set; }
    public CommitTransactionResponse? NextCommitResponse { get; set; } = new();
    public ExecuteStatementRequest? LastExecuteRequest { get; private set; }
    public BeginTransactionRequest? LastBeginRequest { get; private set; }
    public CommitTransactionRequest? LastCommitRequest { get; private set; }
    public RollbackTransactionRequest? LastRollbackRequest { get; private set; }
    public Exception? ExecuteException { get; set; }

    /// <summary>
    /// Sequential exception script: each ExecuteStatementAsync call dequeues one entry —
    /// a non-null entry is thrown, null succeeds. Lets tests script "fails once, then
    /// succeeds" (e.g. the Aurora auto-pause retry). Falls back to ExecuteException.
    /// </summary>
    public Queue<Exception?> ExecuteExceptionScript { get; } = [];

    public int ExecuteCallCount { get; private set; }

    public override Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken cancellationToken = default)
    {
        LastExecuteRequest = request;
        ExecuteCallCount++;
        if (ExecuteExceptionScript.Count > 0)
        {
            var scripted = ExecuteExceptionScript.Dequeue();
            if (scripted is not null) throw scripted;
            return Task.FromResult(NextExecuteResponse ?? new ExecuteStatementResponse());
        }
        if (ExecuteException is not null) throw ExecuteException;
        return Task.FromResult(NextExecuteResponse ?? new ExecuteStatementResponse());
    }

    public override Task<BeginTransactionResponse> BeginTransactionAsync(BeginTransactionRequest request, CancellationToken cancellationToken = default)
    {
        LastBeginRequest = request;
        return Task.FromResult(new BeginTransactionResponse { TransactionId = NextTransactionId });
    }

    public override Task<CommitTransactionResponse> CommitTransactionAsync(CommitTransactionRequest request, CancellationToken cancellationToken = default)
    {
        LastCommitRequest = request;
        return Task.FromResult(NextCommitResponse ?? new CommitTransactionResponse());
    }

    public override Task<RollbackTransactionResponse> RollbackTransactionAsync(RollbackTransactionRequest request, CancellationToken cancellationToken = default)
    {
        LastRollbackRequest = request;
        return Task.FromResult(new RollbackTransactionResponse());
    }
}
