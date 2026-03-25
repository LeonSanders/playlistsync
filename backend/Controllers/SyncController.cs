using Hangfire;
using Microsoft.AspNetCore.Mvc;
using PlaylistSync.Data;
using PlaylistSync.DTOs;
using PlaylistSync.Jobs;
using PlaylistSync.Services;
using Microsoft.EntityFrameworkCore;

namespace PlaylistSync.Controllers;

[ApiController]
[Route("api/sync")]
public class SyncController(
    SyncService syncService,
    AppDbContext db,
    IBackgroundJobClient jobs) : ControllerBase
{
    private string UserId => Request.Cookies["user_id"] ?? "";

    // GET /api/sync/mappings
    [HttpGet("mappings")]
    public async Task<IActionResult> GetMappings()
    {
        if (string.IsNullOrEmpty(UserId)) return Unauthorized();
        var mappings = await db.SyncMappings.Where(m => m.UserId == UserId).ToListAsync();
        return Ok(mappings.Select(m => new MappingDto(
            m.Id, m.SourceService, m.SourcePlaylistId, m.SourcePlaylistName,
            m.TargetService, m.TargetPlaylistId, m.TargetPlaylistName,
            m.Direction.ToString(), m.AutoSync, m.LastSyncedAt, m.LastSyncStatus)));
    }

    // POST /api/sync/mappings
    [HttpPost("mappings")]
    public async Task<IActionResult> CreateMapping([FromBody] CreateMappingRequest req)
    {
        if (string.IsNullOrEmpty(UserId)) return Unauthorized();

        var direction = Enum.Parse<SyncDirection>(req.Direction, ignoreCase: true);
        var mapping = new SyncMapping
        {
            UserId = UserId,
            SourceService = req.SourceService,
            SourcePlaylistId = req.SourcePlaylistId,
            TargetService = req.TargetService,
            TargetPlaylistId = req.TargetPlaylistId,
            Direction = direction,
            AutoSync = req.AutoSync
        };

        db.SyncMappings.Add(mapping);
        await db.SaveChangesAsync();
        return Created($"/api/sync/mappings/{mapping.Id}", mapping.Id);
    }

    // PATCH /api/sync/mappings/{id}
    [HttpPatch("mappings/{id:int}")]
    public async Task<IActionResult> UpdateMapping(int id, [FromBody] UpdateMappingRequest req)
    {
        var mapping = await db.SyncMappings.FirstOrDefaultAsync(m => m.Id == id && m.UserId == UserId);
        if (mapping == null) return NotFound();
        if (req.AutoSync.HasValue) mapping.AutoSync = req.AutoSync.Value;
        if (req.Direction != null) mapping.Direction = Enum.Parse<SyncDirection>(req.Direction, ignoreCase: true);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // DELETE /api/sync/mappings/{id}
    [HttpDelete("mappings/{id:int}")]
    public async Task<IActionResult> DeleteMapping(int id)
    {
        var mapping = await db.SyncMappings.FirstOrDefaultAsync(m => m.Id == id && m.UserId == UserId);
        if (mapping == null) return NotFound();
        db.SyncMappings.Remove(mapping);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // POST /api/sync/mappings/{id}/sync  — trigger manual sync
    [HttpPost("mappings/{id:int}/sync")]
    public async Task<IActionResult> TriggerSync(int id)
    {
        if (string.IsNullOrEmpty(UserId)) return Unauthorized();
        var mapping = await db.SyncMappings.FirstOrDefaultAsync(m => m.Id == id && m.UserId == UserId);
        if (mapping == null) return NotFound();

        var result = await syncService.SyncMappingAsync(id, UserId, SyncTrigger.Manual);
        return Ok(result);
    }

    // GET /api/sync/mappings/{id}/status  — per-track sync status
    [HttpGet("mappings/{id:int}/status")]
    public async Task<IActionResult> GetSyncStatus(int id)
    {
        if (string.IsNullOrEmpty(UserId)) return Unauthorized();
        var status = await syncService.GetTrackSyncStatusAsync(UserId, id);
        return Ok(status);
    }

    // GET /api/sync/logs
    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] int? mappingId, [FromQuery] int limit = 20)
    {
        if (string.IsNullOrEmpty(UserId)) return Unauthorized();
        var query = db.SyncLogs.Where(l => l.UserId == UserId);
        if (mappingId.HasValue) query = query.Where(l => l.MappingId == mappingId);
        var logs = await query.OrderByDescending(l => l.StartedAt).Take(limit).ToListAsync();
        return Ok(logs);
    }


    // POST /api/sync/from-tracks
    // Syncs a caller-supplied track list into a target service playlist.
    // Source can be an imported URL playlist — no auth needed for the source side.
    [HttpPost("from-tracks")]
    public async Task<IActionResult> SyncFromTracks([FromBody] SyncFromTracksRequest req)
    {
        if (string.IsNullOrEmpty(UserId)) return Unauthorized();
        var result = await syncService.SyncFromTracksAsync(
            UserId,
            req.SourceTracks,
            req.TargetService,
            req.TargetPlaylistId,
            req.TargetPlaylistName);
        return Ok(result);
    }

    // POST /api/sync/webhook/spotify  — Spotify sends change notifications here
    [HttpPost("webhook/spotify")]
    public async Task<IActionResult> SpotifyWebhook([FromBody] SpotifyWebhookPayload payload)
    {
        // Spotify sends a challenge on subscription — reflect it back
        if (!string.IsNullOrEmpty(payload.Challenge))
            return Ok(new { challenge = payload.Challenge });

        foreach (var item in payload.Items ?? [])
        {
            var playlistId = item.Uri?.Split(':').LastOrDefault();
            if (string.IsNullOrEmpty(playlistId)) continue;

            var affected = await db.SyncMappings
                .Where(m => m.SourcePlaylistId == playlistId || m.TargetPlaylistId == playlistId)
                .ToListAsync();

            foreach (var mapping in affected)
                jobs.Enqueue<PlaylistSyncJob>(j => j.RunAsync(mapping.Id, mapping.UserId));
        }

        return Accepted();
    }
}

public record UpdateMappingRequest(bool? AutoSync, string? Direction);
public record SpotifyWebhookPayload(string? Challenge, List<SpotifyWebhookItem>? Items);
public record SpotifyWebhookItem(string? Uri, string? Type);
