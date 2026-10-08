using GuitoApi.Infrastructure.Postgres;

namespace GuitoApi.Services.Warm;

/// <summary>Thin adapter over IPostgresDataApiClient for the /warm wake (issue #109).</summary>
public class WarmUpService : IWarmUpService
{
    private static readonly TimeSpan PingCap = TimeSpan.FromSeconds(10);
    /// <summary>Cheap statement; its only job is forcing the cluster's resume from 0 ACU.</summary>
    private const string PingSql = "SELECT 1";

    private readonly IPostgresDataApiClient _postgres;
    private readonly ILogger<WarmUpService> _logger;

    public WarmUpService(IPostgresDataApiClient postgres, ILogger<WarmUpService> logger)
    {
        _postgres = postgres;
        _logger = logger;
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        using var pingCap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pingCap.CancelAfter(PingCap);
        try
        {
            await _postgres.ExecuteAsync(PingSql, cancellationToken: pingCap.Token);
        }
        catch (OperationCanceledException) when (pingCap.IsCancellationRequested)
        {
            // The wake didn't finish within the cap — the next real DB call
            // resumes the cluster itself; /warm's contract is best effort.
        }
        catch (Exception ex)
        {
            // Same: a broken datastore is not /warm's alarm to raise.
            _logger.LogWarning(ex, "/warm Postgres ping failed — container still warmed");
        }
    }
}
