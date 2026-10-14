using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using GuitoApi.IntegrationTests.Common;
using Xunit;

namespace GuitoApi.GuardTests;

public class DisposableExpenseFixtureTests
{
    [Fact]
    public async Task CleanupAsync_ShouldRefuseDelete_WhenOnlyAuditRevisionChanges()
    {
        // Arrange
        using var transport = new ExpenseTransport();
        using var client = CreateClient(transport);
        var fixture = await CreateVerifiedAsync(client);
        transport.Revision = "2";

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CleanupAsync(client));

        // Assert
        Assert.Empty(transport.DeleteRevisions);
        Assert.False(transport.Deleted);
    }

    [Fact]
    public async Task CleanupAsync_ShouldPinIfMatchAndVerifyAbsence_WhenRevisionIsUnchanged()
    {
        // Arrange
        using var transport = new ExpenseTransport();
        using var client = CreateClient(transport);
        var fixture = await CreateVerifiedAsync(client);

        // Act
        await fixture.CleanupAsync(client);

        // Assert
        Assert.Equal("\"1\"", Assert.Single(transport.DeleteRevisions));
        Assert.Equal(new[] { "/Expense/7", "/Expense/latest/100" }, transport.AbsenceReads);
    }

    [Fact]
    public async Task VerifyCreatedAsync_ShouldKeepFirstRevision_WhenVerificationIsRepeatedAfterDrift()
    {
        // Arrange
        using var transport = new ExpenseTransport();
        using var client = CreateClient(transport);
        var fixture = await CreateVerifiedAsync(client);
        transport.Revision = "2";

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.VerifyCreatedAsync(client));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CleanupAsync(client));

        // Assert
        Assert.Empty(transport.DeleteRevisions);
    }

    [Fact]
    public async Task CleanupAsync_ShouldPreserveOwnedCleanup_WhenListVerificationFailsAfterDetail()
    {
        // Arrange
        using var transport = new ExpenseTransport { FailInitialList = true };
        using var client = CreateClient(transport);
        var fixture = new DisposableExpenseFixture("Existing category");
        await fixture.CreateAsync(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.VerifyCreatedAsync(client));

        // Act
        await fixture.CleanupAsync(client);

        // Assert
        Assert.Equal("\"1\"", Assert.Single(transport.DeleteRevisions));
        Assert.Equal(2, transport.AbsenceReads.Count);
    }

    [Theory]
    [InlineData("unread")]
    [InlineData("missing")]
    [InlineData("weak")]
    [InlineData("mismatched")]
    public async Task CleanupAsync_ShouldRefuseDelete_WhenInitialReadIsUncertain(string failure)
    {
        // Arrange
        using var transport = new ExpenseTransport { InitialFailure = failure };
        using var client = CreateClient(transport);
        var fixture = new DisposableExpenseFixture("Existing category");
        await fixture.CreateAsync(client);
        if (failure != "unread") await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.VerifyCreatedAsync(client));

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CleanupAsync(client));

        // Assert
        Assert.Empty(transport.DeleteRevisions);
    }

    private static HttpClient CreateClient(HttpMessageHandler transport) =>
        new(transport) { BaseAddress = new Uri("https://hermetic.invalid") };

    private static async Task<DisposableExpenseFixture> CreateVerifiedAsync(HttpClient client)
    {
        var fixture = new DisposableExpenseFixture("Existing category");
        await fixture.CreateAsync(client);
        await fixture.VerifyCreatedAsync(client);
        return fixture;
    }

    private sealed class ExpenseTransport : HttpMessageHandler
    {
        private JsonElement _created;
        public string Revision { get; set; } = "1";
        public bool FailInitialList { get; init; }
        public string? InitialFailure { get; init; }
        public bool Deleted { get; private set; }
        public List<string> DeleteRevisions { get; } = [];
        public List<string> AbsenceReads { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                _created = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
                return JsonResponse(new { id = 7 });
            }
            if (request.Method == HttpMethod.Delete) return Delete(request);
            if (Deleted) AbsenceReads.Add(path);
            if (path == "/Expense/latest/100") return Latest();
            if (Deleted) return new(HttpStatusCode.NotFound);
            return Detail();
        }

        private HttpResponseMessage Latest() =>
            FailInitialList && !Deleted ? new(HttpStatusCode.ServiceUnavailable)
                : JsonResponse(new { expenses = Deleted ? Array.Empty<object>() : [new { id = 7 }] });

        private HttpResponseMessage Delete(HttpRequestMessage request)
        {
            DeleteRevisions.Add(request.Headers.IfMatch.Single().ToString());
            Deleted = true;
            return new(HttpStatusCode.NoContent);
        }

        private HttpResponseMessage Detail()
        {
            var response = JsonResponse(new
            {
                id = 7, description = _created.GetProperty("description").GetString(),
                category = _created.GetProperty("category").GetString(), amount = _created.GetProperty("amount").GetString(),
                bookingDate = $"{_created.GetProperty("bookingDate").GetString()}T00:00:00+00:00",
                revision = Revision, updateDate = Revision == "1" ? null : "2026-10-14T12:00:00+01:00"
            });
            if (InitialFailure != "missing") response.Headers.ETag = new EntityTagHeaderValue(
                $"\"{(InitialFailure == "mismatched" ? "other" : Revision)}\"", InitialFailure == "weak");
            return response;
        }

        private static HttpResponseMessage JsonResponse(object payload) =>
            new(HttpStatusCode.OK) { Content = DeployedEndpointFixture.ToJsonContent(payload) };
    }
}
