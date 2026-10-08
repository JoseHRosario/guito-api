using GuitoApi.Infrastructure.Postgres;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers;

/// <summary>
/// Public warm endpoint (issue #109): one anonymous GET warms the stack's
/// cold-start costs — the app container (this invocation) and the Aurora
/// min-0-ACU resume (one cheap statement through the Data API client).
/// The ping is AWAITED, bounded by a ~10s cap: a task kicked off after the
/// handler returns is not guaranteed to run in Lambda (the container freezes),
/// so "fire-and-forget server-side" would silently skip the wake. Best effort:
/// any ping failure still leaves the container warm — /warm returns 200.
/// </summary>
[Route("[controller]")]
public class WarmController : ControllerBase
{
    private static readonly TimeSpan PingCap = TimeSpan.FromSeconds(10);
    private readonly IPostgresDataApiClient _postgres;
    private readonly ILogger<WarmController> _logger;

    public WarmController(IPostgresDataApiClient postgres, ILogger<WarmController> logger)
    {
        _postgres = postgres;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> WarmAsync(CancellationToken cancellationToken)
    {
        using var pingCap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pingCap.CancelAfter(PingCap);
        try
        {
            await _postgres.ExecuteAsync("SELECT 1", cancellationToken: pingCap.Token);
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
        return Ok();
    }
}
