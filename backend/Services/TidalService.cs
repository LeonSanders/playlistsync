using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlaylistSync.Data;
using PlaylistSync.DTOs;
using Microsoft.EntityFrameworkCore;

namespace PlaylistSync.Services;

public class TidalService(IConfiguration config, AppDbContext db, HttpClient http, ILogger<TidalService> logger)
{
    private readonly string _clientId = config["Tidal:ClientId"]!;
    private readonly string _redirectUri = config["Tidal:RedirectUri"]!;
    private const string BaseUrl = "https://openapi.tidal.com/v2";
    private const string AuthUrl = "https://login.tidal.com/";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── PKCE helpers ──────────────────────────────────────────────────────────

    public static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Base64UrlEncode(bytes);
    }

    public static string GenerateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Auth ──────────────────────────────────────────────────────────────────

    // Returns (url, codeVerifier) — caller stores the verifier in OAuthState.CodeVerifier
    public (string Url, string CodeVerifier) GetAuthorizationUrl(string state)
    {
        var verifier = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);

        var url = $"{AuthUrl}/authorize"
            + $"?response_type=code"
            + $"&client_id={Uri.EscapeDataString(_clientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(_redirectUri)}"
            + $"&scope={Uri.EscapeDataString("playlists.read playlists.write user.read")}"
            + $"&code_challenge={Uri.EscapeDataString(challenge)}"
            + $"&code_challenge_method=S256"
            + $"&state={Uri.EscapeDataString(state)}";

        logger.LogDebug("Tidal auth URL: {Url}", url);
        return (url, verifier);
    }

    // codeVerifier comes from OAuthState.CodeVerifier stored at login time
    public async Task<UserConnection> HandleCallbackAsync(string code, string userId, string codeVerifier)
    {
        var tokenReq = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _redirectUri,
            ["client_id"] = _clientId,
            ["code_verifier"] = codeVerifier,
        });

        var tokenResp = await http.PostAsync($"{AuthUrl}/token", tokenReq);
        if (!tokenResp.IsSuccessStatusCode)
        {
            var err = await tokenResp.Content.ReadAsStringAsync();
            logger.LogError("Tidal token exchange failed {Status}: {Body}", tokenResp.StatusCode, err);
            throw new Exception($"Tidal token exchange failed ({tokenResp.StatusCode}): {err}");
        }

        var tokenJson = await tokenResp.Content.ReadFromJsonAsync<TidalTokenResponse>(JsonOpts)
            ?? throw new Exception("Failed to parse Tidal token response");

        var profile = await GetWithTokenAsync<TidalUserProfile>(tokenJson.AccessToken, "/users/me");

        var conn = await db.UserConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Service == "tidal")
            ?? new UserConnection { UserId = userId, Service = "tidal" };

        conn.AccessToken = tokenJson.AccessToken;
        conn.RefreshToken = tokenJson.RefreshToken ?? "";
        conn.ExpiresAt = DateTime.UtcNow.AddSeconds(tokenJson.ExpiresIn);
        conn.ServiceUserId = profile?.Data?.Id ?? "";
        conn.DisplayName = profile?.Data?.Attributes?.Username ?? "";
        conn.UpdatedAt = DateTime.UtcNow;

        if (conn.Id == 0) db.UserConnections.Add(conn);
        await db.SaveChangesAsync();
        return conn;
    }

    // ── Token refresh ─────────────────────────────────────────────────────────

    private async Task<string> GetAccessTokenAsync(string userId)
    {
        var conn = await db.UserConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Service == "tidal")
            ?? throw new InvalidOperationException("Tidal not connected");

        if (conn.ExpiresAt <= DateTime.UtcNow.AddMinutes(5))
        {
            // Refresh uses client_id only — no secret, no PKCE verifier needed
            var req = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = conn.RefreshToken,
                ["client_id"] = _clientId,
            });
            var resp = await http.PostAsync($"{AuthUrl}/token", req);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                logger.LogError("Tidal token refresh failed {Status}: {Body}", resp.StatusCode, err);
                throw new Exception($"Tidal token refresh failed: {err}");
            }
            var tokens = await resp.Content.ReadFromJsonAsync<TidalTokenResponse>(JsonOpts)!;
            conn.AccessToken = tokens!.AccessToken;
            conn.ExpiresAt = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn);
            conn.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        return conn.AccessToken;
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<T?> GetWithTokenAsync<T>(string token, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Accept", "application/vnd.api+json");
        var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("Tidal GET {Path} → {Status}", path, resp.StatusCode);
            return default;
        }
        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts);
    }

    private async Task<T?> GetAsync<T>(string userId, string path)
    {
        var token = await GetAccessTokenAsync(userId);
        return await GetWithTokenAsync<T>(token, path);
    }

    // ── Playlists / tracks ────────────────────────────────────────────────────

    public async Task<List<PlaylistDto>> GetPlaylistsAsync(string userId, List<SyncMapping> mappings)
    {
        var result = await GetAsync<TidalCollectionResponse<TidalPlaylist>>(userId, "/playlists/me");
        if (result?.Data == null) return [];

        return result.Data.Select(p =>
        {
            var mapping = mappings.FirstOrDefault(m =>
                (m.SourceService == "tidal" && m.SourcePlaylistId == p.Id) ||
                (m.TargetService == "tidal" && m.TargetPlaylistId == p.Id));
            return new PlaylistDto(
                p.Id,
                p.Attributes?.Name ?? "",
                p.Attributes?.NumberOfTracks ?? 0,
                p.Attributes?.ImageLinks?.FirstOrDefault()?.Href,
                mapping != null,
                mapping?.LastSyncedAt,
                mapping?.LastSyncStatus ?? "never"
            );
        }).ToList();
    }

    public async Task<List<TrackDto>> GetPlaylistTracksAsync(string userId, string playlistId)
    {
        var result = await GetAsync<TidalCollectionResponse<TidalTrack>>(userId, $"/playlists/{playlistId}/items");
        if (result?.Data == null) return [];

        return result.Data.Select(t => new TrackDto(
            t.Id,
            t.Attributes?.Title ?? "",
            t.Attributes?.ArtistName ?? "",
            t.Attributes?.AlbumTitle,
            t.Attributes?.Isrc,
            t.Attributes?.ImageLinks?.FirstOrDefault()?.Href,
            t.Attributes?.Duration ?? 0
        )).ToList();
    }

    public async Task<string> CreatePlaylistAsync(string userId, string name)
    {
        var token = await GetAccessTokenAsync(userId);
        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/playlists");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Accept", "application/vnd.api+json");
        req.Content = JsonContent.Create(new
        {
            data = new { type = "playlists", attributes = new { name, description = "Synced by PlaylistSync" } }
        });
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TidalSingleResponse<TidalPlaylist>>(JsonOpts);
        return body!.Data!.Id;
    }

    public async Task AddTracksAsync(string userId, string playlistId, IEnumerable<string> tidalTrackIds)
    {
        var token = await GetAccessTokenAsync(userId);
        foreach (var chunk in tidalTrackIds.Chunk(50))
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/playlists/{playlistId}/items");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Accept", "application/vnd.api+json");
            req.Content = JsonContent.Create(new
            {
                data = chunk.Select(id => new { type = "tracks", id }).ToArray()
            });
            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
        }
    }

    public async Task<TrackDto?> SearchTrackAsync(string userId, string name, string artist, string? isrc)
    {
        var token = await GetAccessTokenAsync(userId);

        if (!string.IsNullOrEmpty(isrc))
        {
            var isrcResult = await GetWithTokenAsync<TidalCollectionResponse<TidalTrack>>(
                token, $"/tracks?filter[isrc]={Uri.EscapeDataString(isrc)}");
            var isrcTrack = isrcResult?.Data?.FirstOrDefault();
            if (isrcTrack != null) return MapTrack(isrcTrack);
        }

        var query = Uri.EscapeDataString($"{name} {artist}");
        var result = await GetWithTokenAsync<TidalSearchResponse>(token, $"/search?query={query}&type=tracks&limit=5");
        var track = result?.Tracks?.Data?.FirstOrDefault();
        return track == null ? null : MapTrack(track);
    }

    private static TrackDto MapTrack(TidalTrack t) => new(
        t.Id, t.Attributes?.Title ?? "", t.Attributes?.ArtistName ?? "",
        t.Attributes?.AlbumTitle, t.Attributes?.Isrc,
        t.Attributes?.ImageLinks?.FirstOrDefault()?.Href, t.Attributes?.Duration ?? 0);
}

record TidalTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn
);
record TidalCollectionResponse<T>([property: JsonPropertyName("data")] List<T>? Data);
record TidalSingleResponse<T>([property: JsonPropertyName("data")] T? Data);
record TidalSearchResponse([property: JsonPropertyName("tracks")] TidalCollectionResponse<TidalTrack>? Tracks);
record TidalPlaylist(string Id, string Type, TidalPlaylistAttributes? Attributes);
record TidalPlaylistAttributes(string Name, int NumberOfTracks, List<TidalLink>? ImageLinks);
record TidalTrack(string Id, string Type, TidalTrackAttributes? Attributes);
record TidalTrackAttributes(string Title, string ArtistName, string? AlbumTitle, string? Isrc, int Duration, List<TidalLink>? ImageLinks);
record TidalLink(string Href);
record TidalUserProfile([property: JsonPropertyName("data")] TidalUserData? Data);
record TidalUserData(string Id, TidalUserAttributes? Attributes);
record TidalUserAttributes(string Username);
