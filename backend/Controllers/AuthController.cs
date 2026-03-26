using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PlaylistSync.Data;
using PlaylistSync.Services;

namespace PlaylistSync.Controllers;

[ApiController]
[Route("auth")]
public class AuthController(
    SpotifyService spotify,
    TidalService tidal,
    AppDbContext db,
    IConfiguration config) : ControllerBase
{
    // GET /auth/status
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var userId = GetOrCreateUserId();
        var logger = HttpContext.RequestServices.GetRequiredService<ILogger<AuthController>>();
        var hasCookie = Request.Cookies.ContainsKey("user_id");
        logger.LogInformation("Status check. UserId: {UserId}, HasCookie: {HasCookie}", userId[..8], hasCookie);

        var connections = await db.UserConnections
            .Where(c => c.UserId == userId)
            .ToListAsync();

        var sp = connections.FirstOrDefault(c => c.Service == "spotify");
        var ti = connections.FirstOrDefault(c => c.Service == "tidal");

        return Ok(new
        {
            userId,
            spotify = new { connected = sp != null, displayName = sp?.DisplayName },
            tidal   = new { connected = ti != null, displayName = ti?.DisplayName }
        });
    }

    // GET /auth/spotify/login
    [HttpGet("spotify/login")]
    public async Task<IActionResult> SpotifyLogin()
    {
        var logger = HttpContext.RequestServices.GetRequiredService<ILogger<AuthController>>();
        var userId = GetOrCreateUserId();
        var state  = await CreateOAuthStateAsync(userId, "spotify", codeVerifier: null);
        logger.LogInformation("Spotify login. UserId: {UserId}, State: {State}", userId, state);
        SetUserCookie(userId);
        return Redirect(spotify.GetAuthorizationUrl(state));
    }

    // GET /auth/spotify/callback
    [HttpGet("spotify/callback")]
    public async Task<IActionResult> SpotifyCallback([FromQuery] string code, [FromQuery] string state)
    {
        var logger = HttpContext.RequestServices.GetRequiredService<ILogger<AuthController>>();
        logger.LogInformation("Spotify callback received. State: {State}, HasCode: {HasCode}", state, !string.IsNullOrEmpty(code));

        var allStates = await db.OAuthStates.Where(s => s.Service == "spotify").ToListAsync();
        var totalCount = await db.OAuthStates.CountAsync();
        logger.LogInformation("Spotify OAuthStates in DB: {Count} spotify, {Total} total. States: {States}",
            allStates.Count, totalCount,
            string.Join(", ", allStates.Select(s => $"{s.State[..8]}… (expires {s.ExpiresAt:HH:mm:ss} UTC, userId {s.UserId[..8]}…)")));

        var oauthState = await ValidateAndConsumeStateAsync(state, "spotify");
        if (oauthState == null)
        {
            logger.LogWarning("State validation failed. Received: {State}", state);
            return Redirect(config["Frontend:Url"] + "?error=oauth_state_invalid");
        }

        try
        {
            await spotify.HandleCallbackAsync(code, oauthState.UserId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Spotify callback failed after state validation");
            return Redirect(config["Frontend:Url"] + "?error=spotify_auth_failed");
        }

        SetUserCookie(oauthState.UserId);
        return Redirect(config["Frontend:Url"] + "?spotify=connected");
    }

    // GET /auth/tidal/login
    [HttpGet("tidal/login")]
    public async Task<IActionResult> TidalLogin()
    {
        var userId = GetOrCreateUserId();
        var state  = Guid.NewGuid().ToString("N");

        // PKCE: generate verifier here, store it alongside the state
        var (url, codeVerifier) = tidal.GetAuthorizationUrl(state);
        await CreateOAuthStateAsync(userId, "tidal", codeVerifier, state);
        SetUserCookie(userId);
        return Redirect(url);
    }

    // GET /auth/tidal/callback
    [HttpGet("tidal/callback")]
    public async Task<IActionResult> TidalCallback([FromQuery] string code, [FromQuery] string state)
    {
        var oauthState = await ValidateAndConsumeStateAsync(state, "tidal");
        if (oauthState == null) return BadRequest("Invalid or expired OAuth state");

        if (string.IsNullOrEmpty(oauthState.CodeVerifier))
            return BadRequest("Missing PKCE code verifier");

        await tidal.HandleCallbackAsync(code, oauthState.UserId, oauthState.CodeVerifier);
        SetUserCookie(oauthState.UserId);
        return Redirect(config["Frontend:Url"] + "?tidal=connected");
    }

    // DELETE /auth/{service}
    [HttpDelete("{service}")]
    public async Task<IActionResult> Disconnect(string service)
    {
        var userId = GetOrCreateUserId();
        var conn = await db.UserConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Service == service);
        if (conn != null) { db.UserConnections.Remove(conn); await db.SaveChangesAsync(); }
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GetOrCreateUserId()
    {
        if (Request.Cookies.TryGetValue("user_id", out var existing) && !string.IsNullOrEmpty(existing))
            return existing;
        // Fallback: client sends stored ID as header when cookie is missing
        if (Request.Headers.TryGetValue("X-User-Id", out var header) && !string.IsNullOrEmpty(header))
            return header.ToString();
        return Guid.NewGuid().ToString("N");
    }

    private void SetUserCookie(string userId) =>
        Response.Cookies.Append("user_id", userId, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure   = !string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development",
                StringComparison.OrdinalIgnoreCase),
            Expires  = DateTimeOffset.UtcNow.AddYears(1)
        });

    // Overload for Spotify (no PKCE — generates its own state)
    private Task<string> CreateOAuthStateAsync(string userId, string service, string? codeVerifier) =>
        CreateOAuthStateAsync(userId, service, codeVerifier, Guid.NewGuid().ToString("N"));

    private async Task<string> CreateOAuthStateAsync(string userId, string service, string? codeVerifier, string state)
    {
        var logger = HttpContext.RequestServices.GetRequiredService<ILogger<AuthController>>();
        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(10);

        try
        {
            // Clean expired states
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"OAuthStates\" WHERE \"ExpiresAt\" < {0}", now);

            // Insert via EF Core — handles null CodeVerifier correctly
            db.OAuthStates.Add(new OAuthState
            {
                State        = state,
                UserId       = userId,
                Service      = service,
                CodeVerifier = codeVerifier,  // null for Spotify, fine for EF Core
                ExpiresAt    = expiresAt
            });
            await db.SaveChangesAsync();

            var saved = await db.OAuthStates.AnyAsync(s => s.State == state);
            logger.LogInformation("OAuthState saved={Saved}. Service: {Service}, State: {State}, ExpiresAt: {ExpiresAt} UTC",
                saved, service, state, expiresAt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save OAuthState for service {Service}", service);
            throw;
        }

        return state;
    }

    private async Task<OAuthState?> ValidateAndConsumeStateAsync(string state, string service)
    {
        var now = DateTime.UtcNow;
        var record = await db.OAuthStates.FirstOrDefaultAsync(s =>
            s.State   == state &&
            s.Service == service);

        var logger = HttpContext.RequestServices.GetRequiredService<ILogger<AuthController>>();

        if (record == null)
        {
            logger.LogWarning("State not found at all. Now: {Now} UTC", now);
            return null;
        }

        if (record.ExpiresAt < now)
        {
            logger.LogWarning("State expired. ExpiresAt: {ExpiresAt} UTC, Now: {Now} UTC, Diff: {Diff}s",
                record.ExpiresAt, now, (now - record.ExpiresAt).TotalSeconds);
            return null;
        }

        db.OAuthStates.Remove(record);
        await db.SaveChangesAsync();
        return record;
    }
}
