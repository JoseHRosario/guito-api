using GuitoApi.Exceptions;

namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>
    /// Shared classification of upstream EB failures during transaction fetches
    /// (issues #89/#90, ADR-0004): session lost/expired or unknown session → 409
    /// reconnect; rate limit (429, PSD2 ~4 accesses/day per account) → 429 with the
    /// upstream wording; anything else is a provider failure (502). Lives with the
    /// adapter's exception (type-home convention); consumed by the list adapter and
    /// the sync service.
    /// </summary>
    public static class EnableBankingFetchErrorMapper
    {
        public static ProblemException ToProblemException(EnableBankingApiException exception) =>
            exception.StatusCode switch
            {
                401 or 404 => new BankReconnectionRequiredException(
                    "The bank connection has expired — reconnect Enable Banking to fetch transactions."),
                429 => new ProblemException(429,
                    "The bank is rate-limiting transaction fetches (PSD2 limits) — try again later."),
                _ => new ProblemException(502,
                    $"Enable Banking request failed: {exception.Message}"),
            };
    }
}