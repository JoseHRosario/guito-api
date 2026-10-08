namespace GuitoApi.Services.Warm;

/// <summary>
/// GET /warm (issue #109): warms the stack's cold-start costs — the app
/// container runs this invocation, and the Aurora min-0-ACU cluster is woken
/// by one cheap statement through the Data API client. The ping is AWAITED,
/// bounded by a ~10s cap: a task kicked off after the handler returns is not
/// guaranteed to run in Lambda (the container freezes), so fire-and-forget
/// would silently skip the wake. Best effort: any ping failure still leaves
/// the container warm — the warm result never depends on the datastore.
/// </summary>
public interface IWarmUpService
{
    Task WarmUpAsync(CancellationToken cancellationToken = default);
}
