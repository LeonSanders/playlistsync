using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PlaylistSync.Data;
using PlaylistSync.DTOs;
using PlaylistSync.Services;
using Xunit;

namespace PlaylistSync.Tests;

/// <summary>
/// Unit tests for SyncService internals.
/// Uses a real in-memory DB but mocks the two external service clients.
/// </summary>
public class SyncServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly SpotifyService _spotify;
    private readonly TidalService _tidal;
    private readonly SyncService _sut;
    private const string UserId = "sync-svc-user";

    public SyncServiceTests()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"sync-svc-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(opts);
        _spotify = Substitute.For<SpotifyService>();
        _tidal = Substitute.For<TidalService>();
        _sut = new SyncService(_spotify, _tidal, _db, NullLogger<SyncService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    // ── Bidirectional sync ───────────────────────────────────────────────────

    [Fact]
    public async Task SyncBidirectional_BothSidesInSync_NoChanges()
    {
        var mapping = await SeedMappingAsync(SyncDirection.Bidirectional);
        var track = Builders.Track();

        _spotify.GetPlaylistTracksAsync(UserId, "sp-1").Returns(Tracks(track));
        _tidal.GetPlaylistTracksAsync(UserId, "ti-1").Returns(Tracks(track));

        var result = await _sut.SyncMappingAsync(mapping.Id, UserId, SyncTrigger.Manual);

        result.Success.Should().BeTrue();
        result.TracksAdded.Should().Be(0);
        await _spotify.DidNotReceive().AddTracksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>());
        await _tidal.DidNotReceive().AddTracksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>());
    }

    [Fact]
    public async Task SyncBidirectional_SpotifyHasExtraTrack_AddsToTidal()
    {
        var mapping = await SeedMappingAsync(SyncDirection.Bidirectional);
        var shared = Builders.Track("shared", isrc: "SHARED0001");
        var onlySpotify = Builders.Track("only-sp", isrc: "SPOTIFY0001");
        var tidalMatch = Builders.Track("ti-match", isrc: "SPOTIFY0001");

        _spotify.GetPlaylistTracksAsync(UserId, "sp-1").Returns(Tracks(shared, onlySpotify));
        _tidal.GetPlaylistTracksAsync(UserId, "ti-1").Returns(Tracks(shared));
        _tidal.SearchTrackAsync(UserId, onlySpotify.Name, onlySpotify.Artist, onlySpotify.Isrc)
            .Returns(Task.FromResult<TrackDto?>(tidalMatch));

        var result = await _sut.SyncMappingAsync(mapping.Id, UserId, SyncTrigger.Manual);

        result.TracksAdded.Should().Be(1);
        await _tidal.Received(1).AddTracksAsync(UserId, "ti-1", Arg.Is<IEnumerable<string>>(ids => ids.Contains("ti-match")));
    }

    [Fact]
    public async Task SyncBidirectional_TidalHasExtraTrack_AddsToSpotify()
    {
        var mapping = await SeedMappingAsync(SyncDirection.Bidirectional);
        var shared = Builders.Track("shared", isrc: "SHARED0001");
        var onlyTidal = Builders.Track("only-ti", isrc: "TIDAL00001");
        var spotifyMatch = Builders.Track("sp-match", isrc: "TIDAL00001");

        _spotify.GetPlaylistTracksAsync(UserId, "sp-1").Returns(Tracks(shared));
        _tidal.GetPlaylistTracksAsync(UserId, "ti-1").Returns(Tracks(shared, onlyTidal));
        _spotify.SearchTrackAsync(UserId, onlyTidal.Name, onlyTidal.Artist, onlyTidal.Isrc)
            .Returns(Task.FromResult<TrackDto?>(spotifyMatch));

        var result = await _sut.SyncMappingAsync(mapping.Id, UserId, SyncTrigger.Manual);

        result.TracksAdded.Should().Be(1);
        await _spotify.Received(1).AddTracksAsync(UserId, "sp-1", Arg.Is<IEnumerable<string>>(ids => ids.Contains("sp-match")));
    }

    [Fact]
    public async Task SyncBidirectional_BothSidesDiverged_MergesBoth()
    {
        var mapping = await SeedMappingAsync(SyncDirection.Bidirectional);
        var trackA = Builders.Track("a", isrc: "ISRC000001");
        var trackB = Builders.Track("b", isrc: "ISRC000002");

        _spotify.GetPlaylistTracksAsync(UserId, "sp-1").Returns(Tracks(trackA));
        _tidal.GetPlaylistTracksAsync(UserId, "ti-1").Returns(Tracks(trackB));

        _tidal.SearchTrackAsync(UserId, trackA.Name, trackA.Artist, trackA.Isrc)
            .Returns(Task.FromResult<TrackDto?>(Builders.Track("ti-a", isrc: "ISRC000001")));
        _spotify.SearchTrackAsync(UserId, trackB.Name, trackB.Artist, trackB.Isrc)
            .Returns(Task.FromResult<TrackDto?>(Builders.Track("sp-b", isrc: "ISRC000002")));

        var result = await _sut.SyncMappingAsync(mapping.Id, UserId, SyncTrigger.Manual);

        result.TracksAdded.Should().Be(2);
        await _tidal.Received(1).AddTracksAsync(UserId, "ti-1", Arg.Any<IEnumerable<string>>());
        await _spotify.Received(1).AddTracksAsync(UserId, "sp-1", Arg.Any<IEnumerable<string>>());
    }

    // ── One-way sync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncSourceToTarget_OnlyPushesToTarget()
    {
        var mapping = await SeedMappingAsync(SyncDirection.SourceToTarget);
        var track = Builders.Track("new-sp", isrc: "NEWTRACK001");

        _spotify.GetPlaylistTracksAsync(UserId, "sp-1").Returns(Tracks(track));
        _tidal.GetPlaylistTracksAsync(UserId, "ti-1").Returns(Tracks());
        _tidal.SearchTrackAsync(UserId, track.Name, track.Artist, track.Isrc)
            .Returns(Task.FromResult<TrackDto?>(Builders.Track("ti-new")));

        var result = await _sut.SyncMappingAsync(mapping.Id, UserId, SyncTrigger.Manual);

        result.TracksAdded.Should().Be(1);
        await _spotify.DidNotReceive().AddTracksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>());
    }

    [Fact]
    public async Task SyncTargetToSource_OnlyPushesToSource()
    {
        var mapping = await SeedMappingAsync(SyncDirection.TargetToSource);
        var track = Builders.Track("new-ti", isrc: "NEWTRACK002");

        _tidal.GetPlaylistTracksAsync(UserId, "ti-1").Returns(Tracks(track));
        _spotify.GetPlaylistTracksAsync(UserId, "sp-1").Returns(Tracks());
        _spotify.SearchTrackAsync(UserId, track.Name, track.Artist, track.Isrc)
            .Returns(Task.FromResult<TrackDto?>(Builders.Track("sp-new")));

        var result = await _sut.SyncMappingAsync(mapping.Id, UserId, SyncTrigger.Manual);

        result.TracksAdded.Should().Be(1);
        await _tidal.DidNotReceive().AddTracksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IEnumerable<string>>());
    }

    // ── ISRC matching ────────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_IsrcMatchSkipsDuplicates()
    {
        var mapping = await SeedMappingAsync(SyncDirection.SourceToTarget);

        // Same ISRC, different track IDs (normal for cross-service)
        var spTrack = Builders.Track("sp-track", isrc: "USUM71703861");
        var tiTrack = Builders.Track("ti-track", isrc: "USUM71703861");

        _spotify.GetPlaylistTracksAsync(UserId, "sp-1").Returns(Tracks(spTrack));
        _tidal.GetPlaylistTracksAsync(UserId, "ti-1").Returns(Tracks(tiTrack));

        var result = await _sut.SyncMappingAsync(mapping.Id, UserId, SyncTrigger.Manual);

        result.TracksAdded.Should().Be(0);
        await _tidal.DidNotReceive().SearchTrackAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Sync_NoIsrc_FallsBackToSearchAndMatches()
    {
        var mapping = await SeedMappingAsync(SyncDirection.SourceToTarget);
        var noIsrc = Builders.TrackNoIsrc();

        _spotify.GetPlaylistTracksAsync(UserId, "sp-1").Returns(Tracks(noIsrc));
        _tidal.GetPlaylistTracksAsync(UserId, "ti-1").Returns(Tracks());
        _tidal.SearchTrackAsync(UserId, noIsrc.Name, noIsrc.Artist, null)
            .Returns(Task.FromResult<TrackDto?>(Builders.Track("ti-found")));

        var result = await _sut.SyncMappingAsync(mapping.Id, UserId, SyncTrigger.Manual);

        result.TracksAdded.Should().Be(1);
        result.TracksSkipped.Should().Be(0);
    }

    // ── Track sync status ────────────────────────────────────────────────────

    [Fact]
    public async Task GetTrackSyncStatus_MatchedByIsrc_ReportsSynced()
    {
        var mapping = await SeedMappingAsync(SyncDirection.Bidirectional);
        var isrc = "USUM71703861";
        _spotify.GetPlaylistTracksAsync(UserId, "sp-1").Returns(Tracks(Builders.Track("sp-1", isrc: isrc)));
        _tidal.GetPlaylistTracksAsync(UserId, "ti-1").Returns(Tracks(Builders.Track("ti-1", isrc: isrc)));

        var status = await _sut.GetTrackSyncStatusAsync(UserId, mapping.Id);

        status.Should().HaveCount(1);
        status[0].Status.Should().Be("synced");
    }

    [Fact]
    public async Task GetTrackSyncStatus_MatchedByName_ReportsSynced()
    {
        var mapping = await SeedMappingAsync(SyncDirection.Bidirectional);
        _spotify.GetPlaylistTracksAsync(UserId, "sp-1")
            .Returns(Tracks(Builders.TrackNoIsrc("sp-1", "Song Name", "Artist Name")));
        _tidal.GetPlaylistTracksAsync(UserId, "ti-1")
            .Returns(Tracks(Builders.TrackNoIsrc("ti-1", "Song Name", "Artist Name")));

        var status = await _sut.GetTrackSyncStatusAsync(UserId, mapping.Id);

        status[0].Status.Should().Be("synced");
    }

    [Fact]
    public async Task GetTrackSyncStatus_NotOnTarget_ReportsMissing()
    {
        var mapping = await SeedMappingAsync(SyncDirection.Bidirectional);
        _spotify.GetPlaylistTracksAsync(UserId, "sp-1")
            .Returns(Tracks(Builders.Track("sp-only", isrc: "UNIQUE00001")));
        _tidal.GetPlaylistTracksAsync(UserId, "ti-1")
            .Returns(Tracks());

        var status = await _sut.GetTrackSyncStatusAsync(UserId, mapping.Id);

        status[0].Status.Should().Be("missing");
    }

    // ── Error handling ───────────────────────────────────────────────────────

    [Fact]
    public async Task SyncMappingAsync_MappingNotFound_ThrowsAndReturnsError()
    {
        var result = await _sut.SyncMappingAsync(99999, UserId, SyncTrigger.Manual);
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SyncMappingAsync_ServiceThrows_SetsErrorStatus()
    {
        var mapping = await SeedMappingAsync(SyncDirection.SourceToTarget);
        _spotify.GetPlaylistTracksAsync(UserId, "sp-1")
            .Returns<Task<List<TrackDto>>>(_ => throw new HttpRequestException("Spotify API down"));

        var result = await _sut.SyncMappingAsync(mapping.Id, UserId, SyncTrigger.Manual);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Spotify API down");

        var db = factory_Db();
        db.SyncMappings.Find(mapping.Id)!.LastSyncStatus.Should().Be("error");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<SyncMapping> SeedMappingAsync(SyncDirection direction)
    {
        var mapping = Builders.Mapping(UserId,
            sourcePlaylistId: "sp-1",
            targetPlaylistId: "ti-1",
            direction: direction);
        _db.SyncMappings.Add(mapping);
        await _db.SaveChangesAsync();
        return mapping;
    }

    private AppDbContext factory_Db() => _db;

    private static Task<List<TrackDto>> Tracks(params TrackDto[] tracks) =>
        Task.FromResult(tracks.ToList());
}
