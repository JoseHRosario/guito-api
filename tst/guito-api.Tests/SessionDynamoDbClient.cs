using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;

namespace GuitoApi.Tests;

internal sealed class SessionDynamoDbClient(UserSessionTestHost.State state) : AmazonDynamoDBClient(
    new BasicAWSCredentials("hermetic", "hermetic"), new AmazonDynamoDBConfig
    { RegionEndpoint = Amazon.RegionEndpoint.EUWest1, MaxErrorRetry = 0 })
{
    private readonly Dictionary<string, Dictionary<string, AttributeValue>> _rows = [];
    public List<string> CredentialsWritten { get; } = [];
    public List<string> TtlsWritten { get; } = [];
    public bool Fail { get; set; }
    public bool CorruptCredential { get; set; }

    public override Task<DescribeTableResponse> DescribeTableAsync(DescribeTableRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Session requests must not use control-plane readiness.");
    public override Task<ScanResponse> ScanAsync(ScanRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Session requests must use a single handle key.");
    public override Task<QueryResponse> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Session requests must not use indexes.");
    public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Session requests must not use transactions.");

    public override async Task<GetItemResponse> GetItemAsync(GetItemRequest request, CancellationToken cancellationToken = default)
    {
        await BeforeAsync(request, cancellationToken);
        Assert.True(request.ConsistentRead);
        lock (_rows) return new() { Item = Copy(_rows.GetValueOrDefault(request.Key["handle_hash"].S) ?? []) };
    }

    public override async Task<PutItemResponse> PutItemAsync(PutItemRequest request, CancellationToken cancellationToken = default)
    {
        await BeforeAsync(request, cancellationToken);
        lock (_rows)
        {
            var hash = request.Item["handle_hash"].S;
            Check(request.ConditionExpression, _rows.GetValueOrDefault(hash) ?? [], request.ExpressionAttributeValues);
            _rows[hash] = Copy(request.Item);
            RecordCredential(request.Item);
            return new();
        }
    }

    public override async Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request, CancellationToken cancellationToken = default)
    {
        await BeforeAsync(request, cancellationToken);
        ValidateUpdate(request);
        var result = ApplyUpdate(request);
        if (state.AfterDynamoRequest is not null) await state.AfterDynamoRequest(request, cancellationToken);
        return result;
    }

    private UpdateItemResponse ApplyUpdate(UpdateItemRequest request)
    {
        lock (_rows)
        {
            var hash = request.Key["handle_hash"].S;
            var row = _rows.GetValueOrDefault(hash) ?? Copy(request.Key);
            Check(request.ConditionExpression, _rows.GetValueOrDefault(hash) ?? [], request.ExpressionAttributeValues);
            ApplyUpdate(row, request);
            _rows[hash] = row;
            var returned = request.ReturnValues == ReturnValue.ALL_NEW ? Copy(row) : [];
            if (CorruptCredential && returned.Count > 0) returned["encrypted_credential"] = new() { S = "v1.corrupt" };
            return new() { Attributes = returned };
        }
    }

    public override async Task<DeleteItemResponse> DeleteItemAsync(DeleteItemRequest request, CancellationToken cancellationToken = default)
    {
        await BeforeAsync(request, cancellationToken);
        lock (_rows)
        {
            var hash = request.Key["handle_hash"].S;
            Check(request.ConditionExpression, _rows.GetValueOrDefault(hash) ?? [], request.ExpressionAttributeValues);
            _rows.Remove(hash);
            return new();
        }
    }

    private async Task BeforeAsync(AmazonWebServiceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (state.DynamoRequests) state.DynamoRequests.Add(request);
        if (state.BeforeDynamoRequest is not null) await state.BeforeDynamoRequest(request, cancellationToken);
        if (Fail) throw new AmazonDynamoDBException("fake database outage fake-refresh-credential");
    }

    private void ApplyUpdate(Dictionary<string, AttributeValue> row, UpdateItemRequest request)
    {
        var parts = request.UpdateExpression.Split(" REMOVE ");
        if (parts[0].StartsWith("SET ")) Assign(row, parts[0][4..], request);
        var removed = request.UpdateExpression.StartsWith("REMOVE ") ? request.UpdateExpression[7..] : parts.ElementAtOrDefault(1);
        if (removed is not null) foreach (var name in removed.Split(',')) row.Remove(ResolveName(name.Trim(), request));
        if (request.UpdateExpression.Contains("encrypted_credential =")) RecordCredential(row);
    }

    private static void Assign(Dictionary<string, AttributeValue> row, string assignments, UpdateItemRequest request)
    {
        foreach (var assignment in assignments.Split(','))
        {
            var pair = assignment.Split('=', StringSplitOptions.TrimEntries);
            row[ResolveName(pair[0], request)] = CopyValue(request.ExpressionAttributeValues[pair[1]]);
        }
    }

    private static void ValidateUpdate(UpdateItemRequest request)
    {
        // TTL is reserved in expressions, but remains valid as a literal item key.
        if (System.Text.RegularExpressions.Regex.IsMatch(request.UpdateExpression,
            @"(?<![#:A-Za-z0-9_])ttl\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new AmazonDynamoDBException("Invalid UpdateExpression: reserved keyword: ttl") { ErrorCode = "ValidationException" };
    }

    private static string ResolveName(string name, UpdateItemRequest request) =>
        name.StartsWith('#') ? request.ExpressionAttributeNames[name] : name;

    private void RecordCredential(Dictionary<string, AttributeValue> row)
    {
        Assert.DoesNotContain("fake-refresh-credential", row["encrypted_credential"].S);
        Assert.NotNull(row["expires_at_ticks"].N);
        Assert.NotNull(row["ttl"].N);
        CredentialsWritten.Add(row["encrypted_credential"].S);
        TtlsWritten.Add(row["ttl"].N);
    }

    private static void Check(string? condition, Dictionary<string, AttributeValue> row, Dictionary<string, AttributeValue>? values)
    {
        if (!new SessionDynamoCondition(condition ?? "", row, values).Evaluate()) throw new ConditionalCheckFailedException("condition failed");
    }

    private static AttributeValue CopyValue(AttributeValue value) => new() { S = value.S, N = value.N };
    private static Dictionary<string, AttributeValue> Copy(Dictionary<string, AttributeValue> item) =>
        item.ToDictionary(pair => pair.Key, pair => CopyValue(pair.Value));
}
