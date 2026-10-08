using GuitoApi.Exceptions;
using GuitoApi.Infrastructure.EnableBanking;

namespace GuitoApi.Services.BankAuth
{
    /// <summary>
    /// Shared mapping of upstream EB failures to the consent flow's error contract
    /// (issue #89): a rejected request/auth code (400/401/404/422) is a client-side
    /// 400 with the upstream wording; anything else is a provider failure (502).
    /// Transaction-fetch failures classify differently (409 reconnect / 429 rate
    /// limit) — see ListTransactionsEnableBankingService.ClassifyException.
    /// </summary>
    internal static class BankAuthErrorMapper
    {
        public static ProblemException ToProblemException(EnableBankingApiException exception) =>
            exception.StatusCode is 400 or 401 or 404 or 422
                ? new ProblemException(400, $"Enable Banking rejected the auth flow: {exception.Message}")
                : new ProblemException(502, $"Enable Banking auth flow request failed: {exception.Message}");
    }
}