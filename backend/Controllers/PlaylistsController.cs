using Microsoft.AspNetCore.Mvc;
using PlaylistSync.Data;
using PlaylistSync.DTOs;
using PlaylistSync.Services;
using Microsoft.EntityFrameworkCore;

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
}
