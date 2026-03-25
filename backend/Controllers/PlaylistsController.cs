using Microsoft.AspNetCore.Mvc;
using PlaylistSync.Data;
using PlaylistSync.DTOs;
using PlaylistSync.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace PlaylistSync.Controllers;

[ApiController]
[Route("api/playlists")]
public class PlaylistsController(
    SpotifyService spotify,
    TidalService tidal,
    AppDbContext db) : ControllerBase
{
    private string UserId => Request.Cookies["user_id"] ?? "";

    // GET /api/playlists/spotify
    [HttpGet("spotify")]
    public async Task<IActionResult> GetSpotify()
    {
        if (string.IsNullOrEmpty(UserId)) return Unauthorized();
        var mappings = await db.SyncMappings.Where(m => m.UserId == UserId).ToListAsync();
        return Ok(await spotify.GetPlaylistsAsync(UserId, mappings));
    }

    // GET /api/playlists/tidal
    [HttpGet("tidal")]
    public async Task<IActionResult> GetTidal()
    {
        if (string.IsNullOrEmpty(UserId)) return Unauthorized();
        var mappings = await db.SyncMappings.Where(m => m.UserId == UserId).ToListAsync();
        return Ok(await tidal.GetPlaylistsAsync(UserId, mappings));
    }

    // GET /api/playlists/{service}/{playlistId}/tracks
    [HttpGet("{service}/{playlistId}/tracks")]
    public async Task<IActionResult> GetTracks(string service, string playlistId)
    {
        if (string.IsNullOrEmpty(UserId)) return Unauthorized();
        var tracks = service == "spotify"
            ? await spotify.GetPlaylistTracksAsync(UserId, playlistId)
            : await tidal.GetPlaylistTracksAsync(UserId, playlistId);
        return Ok(tracks);
    }

    // POST /api/playlists/from-url
    // Accepts a Spotify or Tidal playlist URL, returns playlist metadata + tracks
    // Uses client credentials for public access — no user login required on that service
    [HttpPost("from-url")]
    public async Task<IActionResult> FromUrl([FromBody] FromUrlRequest req)
    {
        if (string.IsNullOrEmpty(UserId)) return Unauthorized();

        var parsed = ParsePlaylistUrl(req.Url);
        if (parsed == null)
            return BadRequest("Couldn't parse a playlist ID from that URL. " +
                "Expected a Spotify or Tidal playlist link.");

        var (service, playlistId) = parsed.Value;

        try
        {
            // For the user's own connected service, use their token
            // For the other service (no account), use app-level client credentials
            var (metadata, tracks) = service == "spotify"
                ? await spotify.GetPlaylistByIdAsync(UserId, playlistId)
                : await tidal.GetPlaylistByIdAsync(UserId, playlistId);

            return Ok(new ImportedPlaylistDto(
                service,
                playlistId,
                metadata.Name,
                metadata.TrackCount,
                metadata.ImageUrl,
                tracks
            ));
        }
        catch (Exception ex)
        {
            return BadRequest($"Failed to fetch playlist: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string service, string id)? ParsePlaylistUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // Spotify: https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M
        var spotifyMatch = Regex.Match(url, @"spotify\.com/playlist/([A-Za-z0-9]+)");
        if (spotifyMatch.Success) return ("spotify", spotifyMatch.Groups[1].Value);

        // Tidal: https://tidal.com/browse/playlist/uuid or https://listen.tidal.com/playlist/uuid
        var tidalMatch = Regex.Match(url, @"tidal\.com(?:/browse)?/playlist/([A-Za-z0-9\-]+)");
        if (tidalMatch.Success) return ("tidal", tidalMatch.Groups[1].Value);

        // Bare ID fallback — if it looks like a Spotify ID (22 chars base62) or Tidal UUID
        if (Regex.IsMatch(url.Trim(), @"^[A-Za-z0-9]{22}$")) return ("spotify", url.Trim());
        if (Regex.IsMatch(url.Trim(), @"^[0-9a-f\-]{36}$")) return ("tidal", url.Trim());

        return null;
    }
}

public record FromUrlRequest(string Url);
public record ImportedPlaylistDto(
    string Service,
    string PlaylistId,
    string Name,
    int TrackCount,
    string? ImageUrl,
    List<TrackDto> Tracks
);
