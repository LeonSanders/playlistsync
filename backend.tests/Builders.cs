using PlaylistSync.Data;
using PlaylistSync.DTOs;

namespace PlaylistSync.Tests;

public static class Builders
{
    public static TrackDto Track(
        string id = "track-1",
        string name = "Test Track",
        string artist = "Test Artist",
        string? isrc = "USUM71703861") =>
        new(id, name, artist, "Test Album", isrc, null, 210000);

    public static TrackDto TrackNoIsrc(
        string id = "track-no-isrc",
        string name = "No ISRC Track",
        string artist = "Some Artist") =>
        new(id, name, artist, null, null, null, 180000);

    public static SyncMapping Mapping(
        string userId = "test-user-123",
        string sourceService = "spotify",
        string sourcePlaylistId = "sp-playlist-1",
        string targetService = "tidal",
        string targetPlaylistId = "ti-playlist-1",
        SyncDirection direction = SyncDirection.Bidirectional,
        bool autoSync = true) =>
        new()
        {
            UserId = userId,
            SourceService = sourceService,
            SourcePlaylistId = sourcePlaylistId,
            SourcePlaylistName = "My Playlist",
            TargetService = targetService,
            TargetPlaylistId = targetPlaylistId,
            TargetPlaylistName = "My Playlist (Tidal)",
            Direction = direction,
            AutoSync = autoSync,
        };

    public static UserConnection SpotifyConnection(string userId = "test-user-123") =>
        new()
        {
            UserId = userId,
            Service = "spotify",
            AccessToken = "fake-spotify-token",
            RefreshToken = "fake-refresh",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            ServiceUserId = "spotify-user-id",
            DisplayName = "Test Spotify User",
        };

    public static UserConnection TidalConnection(string userId = "test-user-123") =>
        new()
        {
            UserId = userId,
            Service = "tidal",
            AccessToken = "fake-tidal-token",
            RefreshToken = "fake-refresh",
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            ServiceUserId = "tidal-user-id",
            DisplayName = "Test Tidal User",
        };
}
