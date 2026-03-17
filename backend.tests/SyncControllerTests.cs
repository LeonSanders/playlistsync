using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PlaylistSync.Data;
using PlaylistSync.DTOs;
using Xunit;

namespace PlaylistSync.Tests;

[Collection("Integration")]
public class SyncControllerTests(PlaylistSyncFactory factory)
{
    private const string UserId = "sync-ctrl-user";

    // ── GET /api/sync/mappings ───────────────────────────────────────────────

    [Fact]
    public async Task GetMappings_NoCookie_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();
        var res = await client.GetAsync("/api/sync/mappings");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMappings_NoMappings_ReturnsEmptyList()
    {
        var client = factory.CreateAuthenticatedClient("empty-mappings-user");
        var res = await client.GetAsync("/api/sync/mappings");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await res.Content.ReadFromJsonAsync<List<object>>();
        list.Should().BeEmpty();
    }

    [Fact]
    public async Task GetMappings_WithMappings_ReturnsOnlyOwnMappings()
    {
        var db = factory.GetDb();
        db.SyncMappings.AddRange(
            Builders.Mapping(UserId),
            Builders.Mapping(UserId, sourcePlaylistId: "sp-2", targetPlaylistId: "ti-2"),
            Builders.Mapping("other-user")   // should NOT appear
        );
        await db.SaveChangesAsync();

        var client = factory.CreateAuthenticatedClient(UserId);
        var res = await client.GetAsync("/api/sync/mappings");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().ToList();
        list.Should().HaveCount(2);
    }

    // ── POST /api/sync/mappings ──────────────────────────────────────────────

    [Fact]
    public async Task CreateMapping_ValidRequest_Returns201AndPersists()
    {
        var client = factory.CreateAuthenticatedClient(UserId);
        var payload = new
        {
            sourceService = "spotify",
            sourcePlaylistId = "sp-new",
            targetService = "tidal",
            targetPlaylistId = "ti-new",
            direction = "Bidirectional",
            autoSync = true
        };

        var res = await client.PostAsJsonAsync("/api/sync/mappings", payload);

        res.StatusCode.Should().Be(HttpStatusCode.Created);

        var db = factory.GetDb();
        db.SyncMappings.Any(m => m.SourcePlaylistId == "sp-new" && m.UserId == UserId)
            .Should().BeTrue();
    }

    [Fact]
    public async Task CreateMapping_NoCookie_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/sync/mappings", new
        {
            sourceService = "spotify", sourcePlaylistId = "x",
            targetService = "tidal", targetPlaylistId = "y",
            direction = "Bidirectional", autoSync = false
        });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── PATCH /api/sync/mappings/{id} ────────────────────────────────────────

    [Fact]
    public async Task UpdateMapping_ToggleAutoSync_Persists()
    {
        var db = factory.GetDb();
        var mapping = Builders.Mapping(UserId);
        mapping.AutoSync = true;
        db.SyncMappings.Add(mapping);
        await db.SaveChangesAsync();

        var client = factory.CreateAuthenticatedClient(UserId);
        var res = await client.PatchAsJsonAsync(
            $"/api/sync/mappings/{mapping.Id}",
            new { autoSync = false });

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        db = factory.GetDb();
        db.SyncMappings.Find(mapping.Id)!.AutoSync.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateMapping_OtherUsersMapping_ReturnsNotFound()
    {
        var db = factory.GetDb();
        var mapping = Builders.Mapping("other-user-update");
        db.SyncMappings.Add(mapping);
        await db.SaveChangesAsync();

        var client = factory.CreateAuthenticatedClient(UserId);
        var res = await client.PatchAsJsonAsync(
            $"/api/sync/mappings/{mapping.Id}",
            new { autoSync = false });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /api/sync/mappings/{id} ───────────────────────────────────────

    [Fact]
    public async Task DeleteMapping_OwnMapping_Returns204AndRemoves()
    {
        var db = factory.GetDb();
        var mapping = Builders.Mapping(UserId, sourcePlaylistId: "sp-to-delete");
        db.SyncMappings.Add(mapping);
        await db.SaveChangesAsync();

        var client = factory.CreateAuthenticatedClient(UserId);
        var res = await client.DeleteAsync($"/api/sync/mappings/{mapping.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        db = factory.GetDb();
        db.SyncMappings.Find(mapping.Id).Should().BeNull();
    }

    [Fact]
    public async Task DeleteMapping_OtherUsersMapping_ReturnsNotFound()
    {
        var db = factory.GetDb();
        var mapping = Builders.Mapping("delete-other-user");
        db.SyncMappings.Add(mapping);
        await db.SaveChangesAsync();

        var client = factory.CreateAuthenticatedClient(UserId);
        var res = await client.DeleteAsync($"/api/sync/mappings/{mapping.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /api/sync/mappings/{id}/sync ────────────────────────────────────

    [Fact]
    public async Task TriggerSync_MappingNotFound_ReturnsNotFound()
    {
        var client = factory.CreateAuthenticatedClient(UserId);
        var res = await client.PostAsync("/api/sync/mappings/999999/sync", null);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TriggerSync_ValidMapping_RunsSyncAndReturnsResult()
    {
        var db = factory.GetDb();
        var mapping = Builders.Mapping(UserId, sourcePlaylistId: "sp-sync", targetPlaylistId: "ti-sync");
        db.SyncMappings.Add(mapping);
        await db.SaveChangesAsync();

        // Both sides have the same track — no changes expected
        var track = Builders.Track();
        factory.SpotifyMock
            .GetPlaylistTracksAsync(UserId, "sp-sync")
            .Returns(Task.FromResult(new List<TrackDto> { track }));
        factory.TidalMock
            .GetPlaylistTracksAsync(UserId, "ti-sync")
            .Returns(Task.FromResult(new List<TrackDto> { track }));

        var client = factory.CreateAuthenticatedClient(UserId);
        var res = await client.PostAsync($"/api/sync/mappings/{mapping.Id}/sync", null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        result.GetProperty("success").GetBoolean().Should().BeTrue();
        result.GetProperty("tracksAdded").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task TriggerSync_NewTrackOnSource_AddsToTarget()
    {
        var db = factory.GetDb();
        var mapping = Builders.Mapping(UserId,
            sourcePlaylistId: "sp-new-track", targetPlaylistId: "ti-new-track",
            direction: SyncDirection.SourceToTarget);
        db.SyncMappings.Add(mapping);
        await db.SaveChangesAsync();

        var newTrack = Builders.Track("sp-track-1", isrc: "USRC12345678");
        factory.SpotifyMock
            .GetPlaylistTracksAsync(UserId, "sp-new-track")
            .Returns(Task.FromResult(new List<TrackDto> { newTrack }));
        factory.TidalMock
            .GetPlaylistTracksAsync(UserId, "ti-new-track")
            .Returns(Task.FromResult(new List<TrackDto>()));   // empty target
        factory.TidalMock
            .SearchTrackAsync(UserId, newTrack.Name, newTrack.Artist, newTrack.Isrc)
            .Returns(Task.FromResult<TrackDto?>(Builders.Track("ti-track-1", isrc: "USRC12345678")));

        var client = factory.CreateAuthenticatedClient(UserId);
        var res = await client.PostAsync($"/api/sync/mappings/{mapping.Id}/sync", null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        result.GetProperty("tracksAdded").GetInt32().Should().Be(1);

        await factory.TidalMock.Received(1)
            .AddTracksAsync(UserId, "ti-new-track", Arg.Any<IEnumerable<string>>());
    }

    [Fact]
    public async Task TriggerSync_UnmatchableTrack_ReportsAsUnmatched()
    {
        var db = factory.GetDb();
        var mapping = Builders.Mapping(UserId,
            sourcePlaylistId: "sp-unmatched", targetPlaylistId: "ti-unmatched",
            direction: SyncDirection.SourceToTarget);
        db.SyncMappings.Add(mapping);
        await db.SaveChangesAsync();

        var track = Builders.TrackNoIsrc("sp-weird-track", "Obscure Song", "Unknown Band");
        factory.SpotifyMock
            .GetPlaylistTracksAsync(UserId, "sp-unmatched")
            .Returns(Task.FromResult(new List<TrackDto> { track }));
        factory.TidalMock
            .GetPlaylistTracksAsync(UserId, "ti-unmatched")
            .Returns(Task.FromResult(new List<TrackDto>()));
        factory.TidalMock
            .SearchTrackAsync(UserId, track.Name, track.Artist, null)
            .Returns(Task.FromResult<TrackDto?>(null));   // no match found

        var client = factory.CreateAuthenticatedClient(UserId);
        var res = await client.PostAsync($"/api/sync/mappings/{mapping.Id}/sync", null);

        var result = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        result.GetProperty("tracksSkipped").GetInt32().Should().Be(1);
        result.GetProperty("unmatchedCount").GetInt32().Should().Be(1);
        result.GetProperty("unmatched").EnumerateArray().First()
            .GetProperty("name").GetString().Should().Be("Obscure Song");
    }

    // ── GET /api/sync/logs ───────────────────────────────────────────────────

    [Fact]
    public async Task GetLogs_NoCookie_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();
        var res = await client.GetAsync("/api/sync/logs");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetLogs_AfterSync_ContainsLogEntry()
    {
        var db = factory.GetDb();
        var mapping = Builders.Mapping(UserId, sourcePlaylistId: "sp-log", targetPlaylistId: "ti-log");
        db.SyncMappings.Add(mapping);
        await db.SaveChangesAsync();

        factory.SpotifyMock.GetPlaylistTracksAsync(UserId, "sp-log")
            .Returns(Task.FromResult(new List<TrackDto>()));
        factory.TidalMock.GetPlaylistTracksAsync(UserId, "ti-log")
            .Returns(Task.FromResult(new List<TrackDto>()));

        var client = factory.CreateAuthenticatedClient(UserId);
        await client.PostAsync($"/api/sync/mappings/{mapping.Id}/sync", null);

        var logsRes = await client.GetAsync("/api/sync/logs");
        var logs = JsonDocument.Parse(await logsRes.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray().ToList();
        logs.Should().NotBeEmpty();
        logs.First().GetProperty("mappingId").GetInt32().Should().Be(mapping.Id);
    }

    // ── POST /api/sync/webhook/spotify ───────────────────────────────────────

    [Fact]
    public async Task SpotifyWebhook_ChallengeRequest_EchoesChallenge()
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/sync/webhook/spotify", new
        {
            challenge = "abc123",
            items = (object?)null
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("challenge").GetString().Should().Be("abc123");
    }

    [Fact]
    public async Task SpotifyWebhook_PlaylistChange_EnqueuesJobForAffectedMappings()
    {
        var db = factory.GetDb();
        db.SyncMappings.Add(Builders.Mapping("webhook-user",
            sourcePlaylistId: "sp-webhook-pl", targetPlaylistId: "ti-webhook-pl"));
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/sync/webhook/spotify", new
        {
            challenge = (string?)null,
            items = new[] { new { uri = "spotify:playlist:sp-webhook-pl", type = "playlist" } }
        });

        res.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
