using GuitoApi.Exceptions;

namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>
    /// Consent-flow error contract at the port boundary (issues #89/#103): a rejected
    /// request/auth code (400/401/404/422) is a client-side 400 with the upstream
    /// wording; anything else is a provider failure (502). Transaction-fetch failures
    /// classify differently (409 reconnect / 429 rate limit / 502) — see
    /// EnableBankingFetchErrorMapper.
    /// </summary>
    public static class EnableBankingConsentErrorMapper
    {
        public static ProblemException ToProblemException(EnableBankingApiException exception) =>
            exception.StatusCode is 400 or 401 or 404 or 422
                ? new ProblemException(400, $"Enable Banking rejected the auth flow: {exception.Message}")
                : new ProblemException(502, $"Enable Banking auth flow request failed: {exception.Message}");
    }
}