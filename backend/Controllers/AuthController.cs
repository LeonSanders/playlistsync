using Microsoft.AspNetCore.Mvc;
using PlaylistSync.Services;
using PlaylistSync.Data;
using Microsoft.EntityFrameworkCore;

namespace PlaylistSync.Controllers;

[ApiController]
[Route("auth")]
public class AuthController(
    SpotifyService spotify,
    TidalService tidal,
    AppDbContext db,
    IConfiguration config) : ControllerBase
{
    // GET /auth/status  — returns connection state for both services
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var userId = GetOrCreateUserId();
        var connections = await db.UserConnections
            .Where(c => c.UserId == userId)
            .ToListAsync();

        var sp = connections.FirstOrDefault(c => c.Service == "spotify");
        var ti = connections.FirstOrDefault(c => c.Service == "tidal");

        return Ok(new
        {
            userId,
            spotify = new { connected = sp != null, displayName = sp?.DisplayName },
            tidal = new { connected = ti != null, displayName = ti?.DisplayName }
        });
    }

    // GET /auth/spotify/login
    [HttpGet("spotify/login")]
    public IActionResult SpotifyLogin()
    {
        var state = Guid.NewGuid().ToString("N");
        Response.Cookies.Append("oauth_state", state, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax });
        return Redirect(spotify.GetAuthorizationUrl(state));
    }

    // GET /auth/spotify/callback
    [HttpGet("spotify/callback")]
    public async Task<IActionResult> SpotifyCallback([FromQuery] string code, [FromQuery] string state)
    {
        if (!ValidateState(state)) return BadRequest("Invalid state");
        var userId = GetOrCreateUserId();
        await spotify.HandleCallbackAsync(code, userId);
        SetUserCookie(userId);
        return Redirect(config["Frontend:Url"] + "?spotify=connected");
    }

    // GET /auth/tidal/login
    [HttpGet("tidal/login")]
    public IActionResult TidalLogin()
    {
        var state = Guid.NewGuid().ToString("N");
        Response.Cookies.Append("oauth_state", state, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax });
        return Redirect(tidal.GetAuthorizationUrl(state));
    }

    // GET /auth/tidal/callback
    [HttpGet("tidal/callback")]
    public async Task<IActionResult> TidalCallback([FromQuery] string code, [FromQuery] string state)
    {
        if (!ValidateState(state)) return BadRequest("Invalid state");
        var userId = GetOrCreateUserId();
        await tidal.HandleCallbackAsync(code, userId);
        SetUserCookie(userId);
        return Redirect(config["Frontend:Url"] + "?tidal=connected");
    }

    // DELETE /auth/{service}  — disconnect a service
    [HttpDelete("{service}")]
    public async Task<IActionResult> Disconnect(string service)
    {
        var userId = GetOrCreateUserId();
        var conn = await db.UserConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Service == service);
        if (conn != null) { db.UserConnections.Remove(conn); await db.SaveChangesAsync(); }
        return NoContent();
    }

    private string GetOrCreateUserId()
    {
        if (Request.Cookies.TryGetValue("user_id", out var existing) && !string.IsNullOrEmpty(existing))
            return existing;
        return Guid.NewGuid().ToString("N");
    }

    private void SetUserCookie(string userId) =>
        Response.Cookies.Append("user_id", userId, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddYears(1)
        });

    private bool ValidateState(string state)
    {
        if (!Request.Cookies.TryGetValue("oauth_state", out var saved)) return false;
        Response.Cookies.Delete("oauth_state");
        return saved == state;
    }
}
