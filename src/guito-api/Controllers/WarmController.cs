using GuitoApi.Services.Warm;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers;

/// <summary>Public warm endpoint (issue #109): thin delegate to IWarmUpService.</summary>
[ApiController]
[Route("[controller]")]
public class WarmController : ControllerBase
{
    private readonly IWarmUpService _warmUpService;

    public WarmController(IWarmUpService warmUpService) => _warmUpService = warmUpService;

    /// <summary>Always 200 — best-effort wake; failures are logged, not surfaced.</summary>
    [HttpGet]
    public async Task<IActionResult> WarmAsync(CancellationToken cancellationToken)
    {
        await _warmUpService.WarmUpAsync(cancellationToken);
        return Ok();
    }
}
