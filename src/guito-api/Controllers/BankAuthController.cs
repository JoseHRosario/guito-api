using GuitoApi.Configuration;
using GuitoApi.DataTransferObjects.Output;
using GuitoApi.Services.BankAuth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
        private readonly IOptions<AppConfigurationOptions> _options;

        public BankAuthController(IGetBankAuthUrlService getBankAuthUrlService,
            IFinishBankAuthService finishBankAuthService,
            IOptions<AppConfigurationOptions> options)
        {
            _getBankAuthUrlService = getBankAuthUrlService;
            _finishBankAuthService = finishBankAuthService;
            _options = options;
        }

        [HttpGet("url")]
        public async Task<BankAuthUrl> GetAuthUrlAsync(string? aspsp, string? country, CancellationToken cancellationToken)
        {
            return await _getBankAuthUrlService.GetAsync(aspsp ?? string.Empty, country ?? string.Empty, cancellationToken);
        }

        /// <summary>
        /// EB redirects the user's browser here (public path — the code is the credential).
        /// Instead of stranding the user on a JSON body (issue #116), the flow 302s back
        /// to the SPA's Bank page with the linked-account count; the UI reads `linked`,
        /// confirms the link and refreshes the pending list.
        /// </summary>
        [HttpGet("callback")]
        public async Task<IActionResult> FinishAuthAsync(string? code, CancellationToken cancellationToken)
        {
            var result = await _finishBankAuthService.FinishAsync(code ?? string.Empty, cancellationToken);
            var uiOrigin = _options.Value.Cors.AllowedOrigins.FirstOrDefault() ?? "/";
            return Redirect($"{uiOrigin}/bank?linked={result.AccountsLinked}");
        }
    }
}