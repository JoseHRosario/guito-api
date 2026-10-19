using System.Diagnostics;
using GuitoApi.Exceptions;

namespace GuitoApi.Services.Auth;

/// <summary>One deadline across readiness, provider and persistence; diagnostics never receive exception objects.</summary>
internal sealed class UserSessionOperation : IDisposable
{
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _bounded;
    private readonly CancellationToken _caller;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly long _started;
    public CancellationToken Token => _bounded.Token;

    public UserSessionOperation(TimeProvider clock, ILogger logger, CancellationToken caller)
    {
        _clock = clock;
        _logger = logger;
        _caller = caller;
        _started = clock.GetTimestamp();
        _deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25), clock);
        _bounded = CancellationTokenSource.CreateLinkedTokenSource(caller, _deadline.Token);
    }

    public async Task<T> RunAsync<T>(string stage, Func<CancellationToken, Task<T>> action)
    {
        try { Token.ThrowIfCancellationRequested(); return await action(Token); }
        catch (Exception exception) { LogFailure(stage, exception); throw; }
    }

    public Task RunAsync(string stage, Func<CancellationToken, Task> action) =>
        RunAsync(stage, async token => { await action(token); return true; });

    public void LogFailure(string stage, Exception exception)
    {
        var category = Category(stage, exception);
        _logger.LogWarning("User session failure stage={Stage} category={Category} type={Type} correlation={Correlation} elapsed_ms={Elapsed}",
            stage, category, exception.GetType().Name, Activity.Current?.TraceId.ToString() ?? "none",
            (long)_clock.GetElapsedTime(_started).TotalMilliseconds);
    }

    private string Category(string stage, Exception exception) => exception switch
    {
        OperationCanceledException when _caller.IsCancellationRequested => "client_cancelled",
        OperationCanceledException => "request_timeout",
        UserSessionException { Error: "service_starting" } => "readiness_timeout",
        UserSessionException { IsConfigurationFailure: true } => "configuration_unavailable",
        UserSessionException { StatusCode: 401 } => "session_rejected",
        _ when stage is "credential_encrypt" or "credential_decrypt" => "configuration_unavailable",
        _ when stage.StartsWith("provider_", StringComparison.Ordinal) => "provider_unavailable",
        _ when stage is "database_readiness" or "lease_acquire" or "session_create" or "session_complete" or "lease_release" or "session_invalidate" => "datastore_unavailable",
        _ => "internal_fault"
    };

    public void Dispose()
    {
        _bounded.Dispose();
        _deadline.Dispose();
    }
}
