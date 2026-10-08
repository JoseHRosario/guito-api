namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>Typed operations over the Enable Banking HTTP API (issue #89, ADR-0004).</summary>
    public interface IEnableBankingClient
    {
        Task<EnableBankingStartAuthorization> StartAuthorizationAsync(
            string aspspName, string aspspCountry, string state, string redirectUrl,
            CancellationToken cancellationToken = default);

        Task<EnableBankingSession> AuthorizeSessionAsync(string code, CancellationToken cancellationToken = default);

        Task<string> GetSessionStatusAsync(string sessionId, CancellationToken cancellationToken = default);

        Task<EnableBankingTransactionsPage> ListTransactionsAsync(
            string accountUid, DateOnly? dateFrom, DateOnly? dateTo, string? continuationKey,
            CancellationToken cancellationToken = default);
    }
}