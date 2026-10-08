namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>
    /// Mints the fresh Enable Banking JWT client assertion carried by every EB request
    /// (issue #89, ADR-0004). One token per request, TTL kept well under EB's 24h cap.
    /// </summary>
    public interface IEnableBankingJwtSigner
    {
        Task<string> CreateTokenAsync(CancellationToken cancellationToken = default);
    }
}