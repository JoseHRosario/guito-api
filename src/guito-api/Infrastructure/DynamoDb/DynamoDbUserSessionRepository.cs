using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using GuitoApi.Configuration;
using GuitoApi.Exceptions;
using GuitoApi.Model;
using GuitoApi.Repositories;
using Microsoft.Extensions.Options;

namespace GuitoApi.Infrastructure.DynamoDb;

public sealed class DynamoDbUserSessionRepository(IAmazonDynamoDB database,
    IOptions<AppConfigurationOptions> options) : IUserSessionRepository
{
    // This cannot collide with an application SHA-256 handle.
    private const string ReadinessHash = "__user_session_readiness__";
    private string TableName => !string.IsNullOrWhiteSpace(options.Value.UserSessions.TableName)
        && !string.IsNullOrWhiteSpace(options.Value.UserSessions.Region)
        ? options.Value.UserSessions.TableName : throw UserSessionException.Unavailable();

    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default) =>
        await ReadAsync(ReadinessHash, cancellationToken);

    public async Task CreateAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        await database.PutItemAsync(new PutItemRequest
        {
            TableName = TableName, ConditionExpression = "attribute_not_exists(handle_hash)",
            Item = new()
            {
                ["handle_hash"] = Text(session.HandleHash), ["subject"] = Text(session.Subject),
                ["email"] = Text(session.Email), ["encrypted_credential"] = Text(session.EncryptedCredential),
                ["expires_at_ticks"] = Ticks(session.ExpiresAt), ["ttl"] = Number(session.ExpiresAt.ToUnixTimeSeconds())
            }
        }, cancellationToken);
    }

    public async Task<UserSession?> AcquireAsync(string handleHash, string lease, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var request = Update(handleHash, "SET refresh_lease = :lease, lease_expires_at_ticks = :lease_expires",
            "attribute_exists(handle_hash) AND expires_at_ticks > :now AND (attribute_not_exists(refresh_lease) OR lease_expires_at_ticks <= :now)");
        request.ExpressionAttributeValues = new() { [":lease"] = Text(lease), [":now"] = Ticks(now), [":lease_expires"] = Ticks(now.AddMinutes(2)) };
        request.ReturnValues = ReturnValue.ALL_NEW;
        try
        {
            var result = await database.UpdateItemAsync(request, cancellationToken);
            return Read(result.Attributes);
        }
        catch (ConditionalCheckFailedException)
        {
            return await ResolveAcquireFailureAsync(handleHash, now, cancellationToken);
        }
    }

    private async Task<UserSession?> ResolveAcquireFailureAsync(string hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = await ReadAsync(hash, cancellationToken);
        if (current.Item is not { Count: > 0 }) return null;
        if (Read(current.Item).ExpiresAt > now) throw UserSessionException.Unavailable();
        var deleted = await DeleteConditionallyAsync(hash, "attribute_exists(handle_hash) AND expires_at_ticks <= :now",
            new() { [":now"] = Ticks(now) }, cancellationToken);
        // Concurrent renewal means expiry is no longer definitive: preserve the browser cookie.
        if (!deleted) throw UserSessionException.Unavailable();
        return null;
    }

    public async Task<bool> CompleteAsync(string handleHash, string lease, string encryptedCredential, DateTimeOffset now,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var request = Update(handleHash,
            "SET encrypted_credential = :credential, expires_at_ticks = :expires, #ttl = :ttl REMOVE refresh_lease, lease_expires_at_ticks",
            "attribute_exists(handle_hash) AND refresh_lease = :lease AND expires_at_ticks > :now AND lease_expires_at_ticks > :now");
        request.ExpressionAttributeNames = new() { ["#ttl"] = "ttl" };
        request.ExpressionAttributeValues = new()
        {
            [":lease"] = Text(lease), [":credential"] = Text(encryptedCredential), [":now"] = Ticks(now),
            [":expires"] = Ticks(expiresAt), [":ttl"] = Number(expiresAt.ToUnixTimeSeconds())
        };
        return await UpdateConditionallyAsync(request, cancellationToken);
    }

    public async Task ReleaseAsync(string handleHash, string lease, CancellationToken cancellationToken = default)
    {
        var request = Update(handleHash, "REMOVE refresh_lease, lease_expires_at_ticks",
            "attribute_exists(handle_hash) AND refresh_lease = :lease");
        request.ExpressionAttributeValues = new() { [":lease"] = Text(lease) };
        await UpdateConditionallyAsync(request, cancellationToken);
    }

    public Task<bool> InvalidateAsync(string handleHash, string lease, CancellationToken cancellationToken = default) =>
        DeleteConditionallyAsync(handleHash, "attribute_exists(handle_hash) AND refresh_lease = :lease",
            new() { [":lease"] = Text(lease) }, cancellationToken);

    public async Task DeleteAsync(string handleHash, CancellationToken cancellationToken = default) =>
        await database.DeleteItemAsync(new DeleteItemRequest { TableName = TableName, Key = Key(handleHash) }, cancellationToken);

    private Task<GetItemResponse> ReadAsync(string hash, CancellationToken cancellationToken) =>
        database.GetItemAsync(new GetItemRequest { TableName = TableName, Key = Key(hash), ConsistentRead = true }, cancellationToken);

    private UpdateItemRequest Update(string hash, string expression, string condition) => new()
    {
        TableName = TableName, Key = Key(hash), UpdateExpression = expression, ConditionExpression = condition
    };

    private async Task<bool> UpdateConditionallyAsync(UpdateItemRequest request, CancellationToken cancellationToken)
    {
        try { await database.UpdateItemAsync(request, cancellationToken); return true; }
        catch (ConditionalCheckFailedException) { return false; }
    }

    private async Task<bool> DeleteConditionallyAsync(string hash, string condition,
        Dictionary<string, AttributeValue> values, CancellationToken cancellationToken)
    {
        try
        {
            await database.DeleteItemAsync(new DeleteItemRequest
            {
                TableName = TableName, Key = Key(hash), ConditionExpression = condition, ExpressionAttributeValues = values
            }, cancellationToken);
            return true;
        }
        catch (ConditionalCheckFailedException) { return false; }
    }

    private static UserSession Read(Dictionary<string, AttributeValue> item) =>
        new(item["handle_hash"].S, item["subject"].S, item["email"].S, item["encrypted_credential"].S,
            new DateTimeOffset(long.Parse(item["expires_at_ticks"].N, CultureInfo.InvariantCulture), TimeSpan.Zero));
    private static Dictionary<string, AttributeValue> Key(string hash) => new() { ["handle_hash"] = Text(hash) };
    private static AttributeValue Text(string value) => new() { S = value };
    private static AttributeValue Number(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static AttributeValue Ticks(DateTimeOffset value) => Number(value.UtcTicks);
}
