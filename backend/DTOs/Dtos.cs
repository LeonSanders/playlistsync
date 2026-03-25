namespace PlaylistSync.DTOs;

public record PlaylistDto(
    string Id,
    string Name,
    int TrackCount,
    string? ImageUrl,
    bool IsMapped,
    DateTime? LastSyncedAt,
    string SyncStatus
);

public record TrackDto(
    string Id,
    string Name,
    string Artist,
    string? Album,
    string? Isrc,
    string? ImageUrl,
    int DurationMs
);

public record TrackSyncStatusDto(
    TrackDto Track,
    string Status   // "synced" | "missing" | "new"
);

public record MappingDto(
    int Id,
    string SourceService,
    string SourcePlaylistId,
    string SourcePlaylistName,
    string TargetService,
    string TargetPlaylistId,
    string TargetPlaylistName,
    string Direction,
    bool AutoSync,
    DateTime? LastSyncedAt,
    string LastSyncStatus
);

public record SyncResultDto(
    bool Success,
    int TracksAdded,
    int TracksRemoved,
    int TracksSkipped,
    int UnmatchedCount,
    List<UnmatchedTrackDto> Unmatched,
    string? Error
);

public record UnmatchedTrackDto(string Name, string Artist, string SourceService);

public record CreateMappingRequest(
    string SourceService,
    string SourcePlaylistId,
    string TargetService,
    string TargetPlaylistId,
    string Direction,
    bool AutoSync
);

public record ConnectionStatusDto(
    bool SpotifyConnected,
    string? SpotifyDisplayName,
    bool TidalConnected,
    string? TidalDisplayName
);

// Used by the "sync from imported playlist" endpoint —
// caller provides the source tracks directly so no service auth is needed for the source side
public record SyncFromTracksRequest(
    List<TrackDto> SourceTracks,
    string TargetService,
    string TargetPlaylistId,   // empty string = create a new playlist
    string TargetPlaylistName, // used when creating a new playlist
    string Direction
);
