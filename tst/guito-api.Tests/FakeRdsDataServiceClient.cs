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
    public ExecuteStatementResponse? NextExecuteResponse { get; set; } = new();
    public string? NextTransactionId { get; set; }
    public CommitTransactionResponse? NextCommitResponse { get; set; } = new();
    public ExecuteStatementRequest? LastExecuteRequest { get; private set; }
    public BeginTransactionRequest? LastBeginRequest { get; private set; }
    public CommitTransactionRequest? LastCommitRequest { get; private set; }
    public RollbackTransactionRequest? LastRollbackRequest { get; private set; }
    public Exception? ExecuteException { get; set; }

    public override Task<ExecuteStatementResponse> ExecuteStatementAsync(ExecuteStatementRequest request, CancellationToken cancellationToken = default)
    {
        LastExecuteRequest = request;
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