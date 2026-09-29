using System.Net;
using System.Text;

namespace GuitoApi.Tests;

/// <summary>
/// Serves canned Google token-endpoint JSON and records every request; tests
/// assert against the recorded requests (URL + form body). Mirrors the
/// FakeSheetsHttpHandler seam but for the token endpoint.
/// </summary>
public class FakeGoogleTokenHttpHandler : HttpMessageHandler
{
    public List<string> RequestUrls { get; } = [];
    public List<string> RequestBodies { get; } = [];

    /// <summary>HTTP status the next request gets; defaults to a successful exchange.</summary>
    public HttpStatusCode NextStatus { get; set; } = HttpStatusCode.OK;
    public string NextBody { get; set; } =
        """{"access_token":"fake-access-token","id_token":"fake-id-token","expires_in":3599,"token_type":"Bearer","scope":"openid email profile"}""";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestUrls.Add(request.RequestUri?.ToString() ?? "");
        RequestBodies.Add(request.Content is null
            ? ""
            : request.Content.ReadAsStringAsync(cancellationToken).Result);
        return Task.FromResult(new HttpResponseMessage(NextStatus)
        {
            Content = new StringContent(NextBody, Encoding.UTF8, "application/json"),
        });
    }
}
