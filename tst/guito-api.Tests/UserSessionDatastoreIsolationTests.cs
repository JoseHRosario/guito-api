using System.Net;
using Amazon.RDSDataService.Model;
using static GuitoApi.Tests.UserSessionBoundaryTests;

namespace GuitoApi.Tests;

public class UserSessionDatastoreIsolationTests
{
    [Fact]
    public async Task Session_ShouldEstablishRefreshAndLogout_WhenAuroraIsResuming()
    {
        // Arrange
        var state = new UserSessionTestHost.State();
        using var host = new UserSessionTestHost(state);
        using var client = Client(host);

        // Act
        var established = await Login(client);
        var lifecycle = await ExerciseDeviceLifecycleAsync(client, Cookie(established));

        // Assert
        Assert.Equal(HttpStatusCode.OK, established.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lifecycle.Refreshed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, lifecycle.LoggedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, lifecycle.Rejected.StatusCode);
        Assert.Empty(state.Statements);
    }

    private static async Task<(HttpResponseMessage Refreshed, HttpResponseMessage LoggedOut, HttpResponseMessage Rejected)>
        ExerciseDeviceLifecycleAsync(HttpClient client, string cookie)
    {
        var refreshed = await Send(client, "refresh", cookie);
        var loggedOut = await Send(client, "logout", cookie);
        var rejected = await Send(client, "refresh", cookie);
        return (refreshed, loggedOut, rejected);
    }
}
