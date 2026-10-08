namespace GuitoApi.Infrastructure.EnableBanking;

/// <summary>
/// One outbound call to the Enable Banking API (issue #89). The transport-level seam:
/// tests fake this interface (like the Sheets fake) — no test ever reaches real banks.
/// </summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">API path, e.g. "sessions" or "accounts/{uid}/transactions".</param>
/// <param name="JsonBody">JSON request body, or null for GET/DELETE.</param>
public record EnableBankingApiRequest(HttpMethod Method, string Path, string? JsonBody);