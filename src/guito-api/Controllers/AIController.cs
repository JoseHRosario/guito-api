using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Services.ArtificialIntelligence;
using Microsoft.AspNetCore.Mvc;
using Output = GuitoApi.DataTransferObjects.Output;
using Input = GuitoApi.DataTransferObjects.Input;

namespace GuitoApi.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class AIController : ControllerBase
    {
        private readonly IExtractMethodService _extractMethodService;

        public AIController(IExtractMethodService extractMethodService)
        {
            _extractMethodService = extractMethodService;
        }

        [HttpPost("extract")]
        public async Task<Output.ExpenseExtracted> ExtractAsync([FromBody] Input.ExpenseExtract input,
            CancellationToken cancellationToken)
        {
            return await _extractMethodService.ExtractMethodAsync(input, cancellationToken);
        }
    }
}
