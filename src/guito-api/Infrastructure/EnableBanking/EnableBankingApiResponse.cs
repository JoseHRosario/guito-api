namespace GuitoApi.Infrastructure.EnableBanking;

/// <summary>Raw response from the Enable Banking API (issue #89).</summary>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="Body">Raw JSON body.</param>
public record EnableBankingApiResponse(int StatusCode, string Body);