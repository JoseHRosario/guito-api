using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using GuitoApi.Infrastructure.Sheets;
using HttpFactory = Google.Apis.Http.IHttpClientFactory;

namespace GuitoApi.Tests;

/// <summary>
/// Stands in for the real Google credential so the whole HTTP pipeline
/// (controller → service → Google Sheets client → HTTP request/response JSON)
/// runs against canned spreadsheet responses instead of the real Sheets API.
/// </summary>
public class FakeGooglesheetsClientProvider : IGooglesheetsClientProvider
{
    private readonly FakeSheetsHttpHandler _handler;

    public FakeGooglesheetsClientProvider(FakeSheetsHttpHandler handler) => _handler = handler;

    public Task<SheetsService> GetAsync(CancellationToken cancellationToken = default)
    {
        var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientFactory = new StubHttpClientFactory(_handler),
            ApplicationName = "Guito API Tests",
        });
        return Task.FromResult(service);
    }

    private sealed class StubHttpClientFactory : HttpFactory
    {
        private readonly FakeSheetsHttpHandler _handler;
        public StubHttpClientFactory(FakeSheetsHttpHandler handler) => _handler = handler;
        public string? ApplicationName { get; set; }

        public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
        {
            var client = (ConfigurableHttpClient?)null;
            var messageHandler = new Google.Apis.Http.ConfigurableMessageHandler(_handler);
            client = new ConfigurableHttpClient(messageHandler);
            return client;
        }
    }
}