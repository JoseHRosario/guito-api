namespace GuitoApi.Infrastructure.EnableBanking
{
    /// <summary>
    /// Transport-level seam over the Enable Banking HTTP API (issue #89, ADR-0004).
    /// The real implementation injects the fresh JWT client assertion per request;
    /// tests script canned responses instead of calling Enable Banking.
    /// </summary>
    public interface IEnableBankingTransport
    {
        Task<EnableBankingApiResponse> SendAsync(
            EnableBankingApiRequest request,
            CancellationToken cancellationToken = default);
    }
}