using GuitoApi.Configuration;
using GuitoApi.Exceptions;
using Microsoft.Extensions.Options;

namespace GuitoApi.Services
{
    /// <summary>
    /// Resolves request ranges against AppConfiguration. Smoke scope (ADR-0009) is
    /// opt-in per request via the <c>X-Sheet-Scope: smoke</c> header: it grants
    /// strictly less than the agent key already does (writing the real tab), so it
    /// needs no authorization of its own. Unconfigured smoke ranges fail closed.
    /// </summary>
    public class SheetScopeResolver : ISheetScopeResolver
    {
        public const string ScopeHeaderKey = "X-Sheet-Scope";
        private const string SmokeScopeValue = "smoke";

        private readonly AppConfigurationOptions _options;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public SheetScopeResolver(
            IOptions<AppConfigurationOptions> options,
            IHttpContextAccessor httpContextAccessor)
        {
            _options = options.Value;
            _httpContextAccessor = httpContextAccessor;
        }

        public bool IsSmokeScope =>
            string.Equals(_httpContextAccessor.HttpContext?.Request.Headers[ScopeHeaderKey].ToString(),
                SmokeScopeValue, StringComparison.OrdinalIgnoreCase);

        public string ExpensesRange => IsSmokeScope ? RequiredSmokeRange(_options.Googlesheets.ExpensesSmokeRange) : _options.Googlesheets.ExpensesRange;

        public string ExpensesDateRange => IsSmokeScope ? RequiredSmokeRange(_options.Googlesheets.ExpensesDateSmokeRange) : _options.Googlesheets.ExpensesDateRange;

        public string ExpensesLatestRange => IsSmokeScope ? RequiredSmokeRange(_options.Googlesheets.ExpensesLatestSmokeRange) : _options.Googlesheets.ExpensesLatestRange;

        private static string RequiredSmokeRange(string configuredRange) =>
            string.IsNullOrWhiteSpace(configuredRange)
                ? throw new ProblemException(500, "Smoke scope is not configured for this environment")
                : configuredRange;
    }
}
