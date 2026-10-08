using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Services.BankConnections;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers
{
    /// <summary>
    /// Linked bank accounts for the Settings bank-connection card (issue #116).
    /// Human-auth path like every other business route.
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class BankConnectionController(IListBankConnectionsService connections) : ControllerBase
    {
        [HttpGet]
        public async Task<BankConnectionList> ListAsync(CancellationToken cancellationToken)
        {
            return await connections.ListAsync(cancellationToken);
        }
    }
}