using GuitoApi.Infrastructure.EnableBanking;

namespace GuitoApi.Tests;

/// <summary>
/// Hermetic fake of IEnableBankingTransport (issue #89): scripts responses per call
/// (queue) and records every request for assertions — mirrors the Sheets fake pattern.
/// </summary>
public class FakeEnableBankingTransport : IEnableBankingTransport
{
    public Queue<EnableBankingApiResponse> NextResponses { get; } = new();
    public Exception? SendException { get; set; }

    public List<EnableBankingApiRequest> Requests { get; } = [];

    public Task<EnableBankingApiResponse> SendAsync(EnableBankingApiRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (SendException is not null)
            throw SendException;
        return Task.FromResult(NextResponses.Count > 0
            ? NextResponses.Dequeue()
            : new EnableBankingApiResponse(200, "{}"));
    }

    /// <summary>Convenience for scripting a canned JSON response.</summary>
    public void Enqueue(int statusCode, string jsonBody) =>
        NextResponses.Enqueue(new EnableBankingApiResponse(statusCode, jsonBody));
}