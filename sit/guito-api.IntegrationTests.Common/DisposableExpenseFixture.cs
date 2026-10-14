using System.Net;
using System.Text.Json;

namespace GuitoApi.IntegrationTests.Common;

/// <summary>Owns one synthetic identity and its first verified revision; never renews deletion intent.</summary>
public sealed class DisposableExpenseFixture(string category)
{
    private const string BookingDate = "2026-01-15";
    private const string Amount = "0.0100";
    private readonly string _description = $"integration-test {Guid.NewGuid():N}";
    private string? _firstRevision;
    public int Id { get; private set; }

    public async Task CreateAsync(HttpClient client)
    {
        using var content = DeployedEndpointFixture.ToJsonContent(new
        {
            bookingDate = BookingDate, amount = Amount, description = _description, category
        });
        using var response = await client.PostAsync("/Expense", content);
        Require(response.StatusCode == HttpStatusCode.OK, "Create was not acknowledged; reconcile before retrying.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Id = document.RootElement.GetProperty("id").GetInt32();
        Require(Id > 0, "Create returned no positive identity; reconcile before retrying.");
    }

    public async Task VerifyCreatedAsync(HttpClient client)
    {
        var revision = await VerifyDetailAsync(client);
        _firstRevision ??= revision;
        Require(revision == _firstRevision, "Revision changed; no cleanup is permitted.");
        Require(await IsListedAsync(client), "Created identity was not listed.");
    }

    public async Task CleanupAsync(HttpClient client)
    {
        Require(_firstRevision != null, "Initial read was uncertain; reconcile manually, no cleanup is permitted.");
        var revision = await VerifyDetailAsync(client);
        Require(revision == _firstRevision, "Revision changed; reconcile manually, no cleanup is permitted.");
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/Expense/{Id}");
        request.Headers.TryAddWithoutValidation("If-Match", _firstRevision);
        using var response = await client.SendAsync(request);
        Require(response.StatusCode == HttpStatusCode.NoContent, "Cleanup was not acknowledged; reconcile manually, never blind retry.");
        await VerifyAbsenceAsync(client);
    }

    private async Task<string> VerifyDetailAsync(HttpClient client)
    {
        Require(Id > 0, "No known created identity; no cleanup is permitted.");
        using var response = await client.GetAsync($"/Expense/{Id}");
        Require(response.StatusCode == HttpStatusCode.OK, "Cannot verify owned identity; no cleanup is permitted.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        VerifyValues(document.RootElement);
        var etag = response.Headers.ETag;
        Require(etag is { IsWeak: false } && etag.ToString() == $"\"{document.RootElement.GetProperty("revision").GetString()}\"",
            "Missing or inconsistent strong revision; no cleanup is permitted.");
        return etag!.ToString();
    }

    private void VerifyValues(JsonElement detail)
    {
        Require(detail.GetProperty("id").GetInt32() == Id
            && string.Equals(detail.GetProperty("description").GetString(), _description, StringComparison.OrdinalIgnoreCase)
            && detail.GetProperty("category").GetString() == category
            && detail.GetProperty("amount").GetString() == Amount
            && detail.GetProperty("bookingDate").GetString() == $"{BookingDate}T00:00:00+00:00",
            "Owned identity no longer has this run's values; no cleanup is permitted.");
    }

    private async Task VerifyAbsenceAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"/Expense/{Id}");
        Require(response.StatusCode == HttpStatusCode.NotFound, "Deleted identity is still readable.");
        Require(!await IsListedAsync(client), "Deleted identity is still listed.");
    }

    private async Task<bool> IsListedAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/Expense/latest/100");
        Require(response.StatusCode == HttpStatusCode.OK, "Cannot verify identity in latest list.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("expenses").EnumerateArray().Any(expense => expense.GetProperty("id").GetInt32() == Id);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
