using System.Net;
using System.Text.Json;
using FluentAssertions;
using PlaylistSync.Data;
using Xunit;

namespace PlaylistSync.Tests;

[Collection("Integration")]
public class AuthControllerTests(PlaylistSyncFactory factory)
{
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task GetStatus_NoConnections_ReturnsBothDisconnected()
    {
        var client = factory.CreateAuthenticatedClient("fresh-user-no-connections");

        var res = await client.GetAsync("/auth/status");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("spotify").GetProperty("connected").GetBoolean().Should().BeFalse();
        body.GetProperty("tidal").GetProperty("connected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetStatus_SpotifyConnected_ReturnsSpotifyConnected()
    {
        var userId = "user-with-spotify";
        var db = factory.GetDb();
        db.UserConnections.Add(Builders.SpotifyConnection(userId));
        await db.SaveChangesAsync();

        var client = factory.CreateAuthenticatedClient(userId);
        var res = await client.GetAsync("/auth/status");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("spotify").GetProperty("connected").GetBoolean().Should().BeTrue();
        body.GetProperty("spotify").GetProperty("displayName").GetString().Should().Be("Test Spotify User");
        body.GetProperty("tidal").GetProperty("connected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetStatus_BothConnected_ReturnsBothConnected()
    {
        var userId = "user-both-connected";
        var db = factory.GetDb();
        db.UserConnections.AddRange(
            Builders.SpotifyConnection(userId),
            Builders.TidalConnection(userId));
        await db.SaveChangesAsync();

        var client = factory.CreateAuthenticatedClient(userId);
        var res = await client.GetAsync("/auth/status");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("spotify").GetProperty("connected").GetBoolean().Should().BeTrue();
        body.GetProperty("tidal").GetProperty("connected").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Disconnect_ExistingService_RemovesConnection()
    {
        var userId = "user-to-disconnect";
        var db = factory.GetDb();
        db.UserConnections.Add(Builders.SpotifyConnection(userId));
        await db.SaveChangesAsync();

        var client = factory.CreateAuthenticatedClient(userId);
        var res = await client.DeleteAsync("/auth/spotify");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify it's gone
        var statusRes = await client.GetAsync("/auth/status");
        var body = JsonDocument.Parse(await statusRes.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("spotify").GetProperty("connected").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Disconnect_NonExistentService_ReturnsNoContent()
    {
        // Disconnecting something that was never connected should be a no-op
        var client = factory.CreateAuthenticatedClient("user-never-connected");
        var res = await client.DeleteAsync("/auth/tidal");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SpotifyLogin_RedirectsToSpotify()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var res = await client.GetAsync("/auth/spotify/login");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.Host.Should().Be("accounts.spotify.com");
    }

    [Fact]
    public async Task TidalLogin_RedirectsToTidal()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        var res = await client.GetAsync("/auth/tidal/login");
        res.StatusCode.Should().Be(HttpStatusCode.Redirect);
        res.Headers.Location!.ToString().Should().Contain("tidal.com");
    }
}
