using PlaylistSync.Data;
using PlaylistSync.DTOs;
using Microsoft.EntityFrameworkCore;

namespace PlaylistSync.Services;

public class SyncService(
    SpotifyService spotify,
    TidalService tidal,
    AppDbContext db,
    ILogger<SyncService> logger)
{
    public async Task<SyncResultDto> SyncMappingAsync(int mappingId, string userId, SyncTrigger trigger)
    {
        var mapping = await db.SyncMappings.FindAsync(mappingId)
            ?? throw new InvalidOperationException($"Mapping {mappingId} not found");

        var log = new SyncLog { MappingId = mappingId, UserId = userId, Trigger = trigger };
        db.SyncLogs.Add(log);
        await db.SaveChangesAsync();

        try
        {
            var result = mapping.Direction switch
            {
                SyncDirection.SourceToTarget => await SyncOneWayAsync(userId, mapping, mapping.SourceService, mapping.TargetService),
                SyncDirection.TargetToSource => await SyncOneWayAsync(userId, mapping, mapping.TargetService, mapping.SourceService),
                SyncDirection.Bidirectional => await SyncBidirectionalAsync(userId, mapping),
                _ => throw new ArgumentOutOfRangeException()
            };

            mapping.LastSyncedAt = DateTime.UtcNow;
            mapping.LastSyncStatus = result.Success ? "synced" : "error";
            log.CompletedAt = DateTime.UtcNow;
            log.TracksAdded = result.TracksAdded;
            log.TracksRemoved = result.TracksRemoved;
            log.TracksSkipped = result.TracksSkipped;
            await db.SaveChangesAsync();
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync failed for mapping {MappingId}", mappingId);
            mapping.LastSyncStatus = "error";
            log.CompletedAt = DateTime.UtcNow;
            log.ErrorMessage = ex.Message;
            await db.SaveChangesAsync();
            return new SyncResultDto(false, 0, 0, 0, 0, [], ex.Message);
        }
    }

    private async Task<SyncResultDto> SyncBidirectionalAsync(string userId, SyncMapping mapping)
    {
        var sourceTracks = await GetTracksAsync(userId, mapping.SourceService, mapping.SourcePlaylistId);
        var targetTracks = await GetTracksAsync(userId, mapping.TargetService, mapping.TargetPlaylistId);

        var sourceIsrcs = sourceTracks.Where(t => t.Isrc != null).Select(t => t.Isrc!).ToHashSet();
        var targetIsrcs = targetTracks.Where(t => t.Isrc != null).Select(t => t.Isrc!).ToHashSet();

        var onlyInSource = sourceTracks.Where(t => t.Isrc == null || !targetIsrcs.Contains(t.Isrc)).ToList();
        var onlyInTarget = targetTracks.Where(t => t.Isrc == null || !sourceIsrcs.Contains(t.Isrc)).ToList();

        var addedToTarget = await AddTracksToServiceAsync(userId, mapping.TargetService, mapping.TargetPlaylistId, onlyInSource);
        var addedToSource = await AddTracksToServiceAsync(userId, mapping.SourceService, mapping.SourcePlaylistId, onlyInTarget);

        return new SyncResultDto(
            true,
            addedToTarget.added + addedToSource.added,
            0,
            addedToTarget.skipped + addedToSource.skipped,
            addedToTarget.unmatched.Count + addedToSource.unmatched.Count,
            [.. addedToTarget.unmatched, .. addedToSource.unmatched],
            null
        );
    }

    private async Task<SyncResultDto> SyncOneWayAsync(string userId, SyncMapping mapping, string fromService, string toService)
    {
        var fromPlaylistId = fromService == mapping.SourceService ? mapping.SourcePlaylistId : mapping.TargetPlaylistId;
        var toPlaylistId = fromService == mapping.SourceService ? mapping.TargetPlaylistId : mapping.SourcePlaylistId;

        var fromTracks = await GetTracksAsync(userId, fromService, fromPlaylistId);
        var toTracks = await GetTracksAsync(userId, toService, toPlaylistId);

        var toIsrcs = toTracks.Where(t => t.Isrc != null).Select(t => t.Isrc!).ToHashSet();
        var toAdd = fromTracks.Where(t => t.Isrc == null || !toIsrcs.Contains(t.Isrc)).ToList();

        var result = await AddTracksToServiceAsync(userId, toService, toPlaylistId, toAdd);
        return new SyncResultDto(true, result.added, 0, result.skipped, result.unmatched.Count, result.unmatched, null);
    }

    private async Task<(int added, int skipped, List<UnmatchedTrackDto> unmatched)> AddTracksToServiceAsync(
        string userId, string service, string playlistId, List<TrackDto> tracks)
    {
        var trackIds = new List<string>();
        var skipped = 0;
        var unmatched = new List<UnmatchedTrackDto>();

        foreach (var track in tracks)
        {
            try
            {
                var match = await SearchTrackAsync(userId, service, track.Name, track.Artist, track.Isrc);
                if (match != null)
                {
                    trackIds.Add(match.Id);
                }
                else
                {
                    skipped++;
                    var reason = track.Isrc != null
                        ? $"Not in {service} catalog (ISRC: {track.Isrc})"
                        : $"Not in {service} catalog (no ISRC — matched by name/artist)";
                    unmatched.Add(new UnmatchedTrackDto(track.Name, track.Artist, service, reason));
                }
            }
            catch (Exception ex)
            {
                skipped++;
                logger.LogWarning("Search failed for '{Name}' by '{Artist}': {Msg}", track.Name, track.Artist, ex.Message);
                unmatched.Add(new UnmatchedTrackDto(track.Name, track.Artist, service, $"Search error: {ex.Message}"));
            }
        }

        if (trackIds.Count > 0)
        {
            if (service == "spotify")
                await spotify.AddTracksAsync(userId, playlistId, trackIds);
            else
                await tidal.AddTracksAsync(userId, playlistId, trackIds);
        }

        return (trackIds.Count, skipped, unmatched);
    }

    public async Task<List<TrackSyncStatusDto>> GetTrackSyncStatusAsync(string userId, int mappingId)
    {
        var mapping = await db.SyncMappings.FindAsync(mappingId)
            ?? throw new InvalidOperationException("Mapping not found");

        // Gracefully handle case where one service isn't connected
        List<TrackDto> sourceTracks = [];
        List<TrackDto> targetTracks = [];
        try { sourceTracks = await GetTracksAsync(userId, mapping.SourceService, mapping.SourcePlaylistId); }
        catch (InvalidOperationException ex) { logger.LogWarning("Source not connected for status check: {Msg}", ex.Message); }
        try { targetTracks = await GetTracksAsync(userId, mapping.TargetService, mapping.TargetPlaylistId); }
        catch (InvalidOperationException ex) { logger.LogWarning("Target not connected for status check: {Msg}", ex.Message); }

        var targetIsrcs = targetTracks.Where(t => t.Isrc != null).Select(t => t.Isrc!).ToHashSet();
        var targetNames = targetTracks.Select(t => $"{t.Name}|{t.Artist}".ToLower()).ToHashSet();

        return sourceTracks.Select(t =>
        {
            var status = "missing";
            if (t.Isrc != null && targetIsrcs.Contains(t.Isrc))
                status = "synced";
            else if (targetNames.Contains($"{t.Name}|{t.Artist}".ToLower()))
                status = "synced";
            return new TrackSyncStatusDto(t, status);
        }).ToList();
    }

    private async Task<List<TrackDto>> GetTracksAsync(string userId, string service, string playlistId) =>
        service == "spotify"
            ? await spotify.GetPlaylistTracksAsync(userId, playlistId)
            : await tidal.GetPlaylistTracksAsync(userId, playlistId);

    private async Task<TrackDto?> SearchTrackAsync(string userId, string service, string name, string artist, string? isrc) =>
        service == "spotify"
            ? await spotify.SearchTrackAsync(userId, name, artist, isrc)
            : await tidal.SearchTrackAsync(userId, name, artist, isrc);
    /// Syncs tracks into a target service playlist.
    /// If sourceTracks is empty and sourceService/sourcePlaylistId are provided,
    /// fetches tracks from the source service directly (no URL import needed).
    public async Task<SyncResultDto> SyncFromTracksAsync(
        string userId,
        List<TrackDto> sourceTracks,
        string targetService,
        string targetPlaylistId,
        string targetPlaylistName,
        string? sourceService = null,
        string? sourcePlaylistId = null)
    {
        // Fetch source tracks from service if not provided directly
        if (sourceTracks.Count == 0 && !string.IsNullOrEmpty(sourceService) && !string.IsNullOrEmpty(sourcePlaylistId))
        {
            sourceTracks = await GetTracksAsync(userId, sourceService, sourcePlaylistId);
            logger.LogInformation("Fetched {Count} tracks from {Service}/{Playlist}",
                sourceTracks.Count, sourceService, sourcePlaylistId);
        }

        // Create target playlist if no ID provided
        if (string.IsNullOrEmpty(targetPlaylistId))
        {
            targetPlaylistId = targetService == "spotify"
                ? await spotify.CreatePlaylistAsync(userId, targetPlaylistName)
                : await tidal.CreatePlaylistAsync(userId, targetPlaylistName);
            logger.LogInformation("Created {Service} playlist '{Name}' → {Id}",
                targetService, targetPlaylistName, targetPlaylistId);
        }

        // Avoid duplicates
        var existingTracks = await GetTracksAsync(userId, targetService, targetPlaylistId);
        var existingIsrcs  = existingTracks.Where(t => t.Isrc != null).Select(t => t.Isrc!).ToHashSet();
        var existingNames  = existingTracks.Select(t => $"{t.Name}|{t.Artist}".ToLower()).ToHashSet();

        var toAdd = sourceTracks.Where(t =>
            (t.Isrc == null || !existingIsrcs.Contains(t.Isrc)) &&
            !existingNames.Contains($"{t.Name}|{t.Artist}".ToLower())
        ).ToList();

        var result = await AddTracksToServiceAsync(userId, targetService, targetPlaylistId, toAdd);

        return new SyncResultDto(true, result.added, 0, result.skipped, result.unmatched.Count, result.unmatched, null);
    }

}
