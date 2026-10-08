using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Services.BankAuth;
using Microsoft.AspNetCore.Mvc;

namespace GuitoApi.Controllers
{
    /// <summary>
    /// Bank consent flow endpoints (issue #89, ADR-0004): the SPA asks for the auth
    /// redirect URL; the EB-registered callback URL lands here with the auth code.
    /// Both go through the human-auth middleware like every other route.
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class BankAuthController : ControllerBase
    {
        private readonly IGetBankAuthUrlService _getBankAuthUrlService;
        private readonly IFinishBankAuthService _finishBankAuthService;

        public BankAuthController(IGetBankAuthUrlService getBankAuthUrlService,
            IFinishBankAuthService finishBankAuthService)
        {
            _getBankAuthUrlService = getBankAuthUrlService;
            _finishBankAuthService = finishBankAuthService;
        }

        [HttpGet("url")]
        public async Task<BankAuthUrl> GetAuthUrlAsync(string? aspsp, string? country, CancellationToken cancellationToken)
        {
            return await _getBankAuthUrlService.GetAsync(aspsp ?? string.Empty, country ?? string.Empty, cancellationToken);
        }

        [HttpGet("callback")]
        public async Task<BankAuthResult> FinishAuthAsync(string? code, CancellationToken cancellationToken)
        {
            return await _finishBankAuthService.FinishAsync(code ?? string.Empty, cancellationToken);
        }
    }
}