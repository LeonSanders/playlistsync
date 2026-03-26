using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlaylistSync.Data;
using PlaylistSync.DTOs;
using Microsoft.EntityFrameworkCore;

namespace PlaylistSync.Services;

public class TidalService(IConfiguration config, AppDbContext db, HttpClient http, ILogger<TidalService> logger, TidalThrottler throttler)
{
    private readonly string _clientId    = config["Tidal:ClientId"]!;
    private readonly string _redirectUri = config["Tidal:RedirectUri"]!;
    // countryCode is required on every Tidal API call — default US, override in appsettings if needed
    private readonly string _countryCode = config["Tidal:CountryCode"] ?? "US";
    private readonly IConfiguration _config = config;
    private const string BaseUrl = "https://openapi.tidal.com/v2";
    private const string AuthUrl = "https://login.tidal.com";

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

    public (string Url, string CodeVerifier) GetAuthorizationUrl(string state)
    {
        var verifier  = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);

        var url = $"{AuthUrl}/oauth2/authorize"
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

    public async Task<UserConnection> HandleCallbackAsync(string code, string userId, string codeVerifier)
    {
        var tokenReq = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["code"]          = code,
            ["redirect_uri"]  = _redirectUri,
            ["client_id"]     = _clientId,
            ["code_verifier"] = codeVerifier,
        });

        var tokenMsg = new HttpRequestMessage(HttpMethod.Post, $"{AuthUrl}/oauth2/token")
        {
            Content = tokenReq
        };
        tokenMsg.Headers.Add("Referer", "https://login.tidal.com/");
        var tokenResp = await http.SendAsync(tokenMsg);
        if (!tokenResp.IsSuccessStatusCode)
        {
            var err = await tokenResp.Content.ReadAsStringAsync();
            logger.LogError("Tidal token exchange failed {Status}: {Body}", tokenResp.StatusCode, err);
            throw new Exception($"Tidal token exchange failed ({tokenResp.StatusCode}): {err}");
        }

        var tokenJson = await tokenResp.Content.ReadFromJsonAsync<TidalTokenResponse>(JsonOpts)
            ?? throw new Exception("Failed to parse Tidal token response");

        // GET /v2/users/me to fetch display name
        var profile = await GetJsonApiAsync<TidalUserResource>(tokenJson.AccessToken, "/users/me");

        var conn = await db.UserConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Service == "tidal")
            ?? new UserConnection { UserId = userId, Service = "tidal" };

        conn.AccessToken   = tokenJson.AccessToken;
        conn.RefreshToken  = tokenJson.RefreshToken ?? "";
        conn.ExpiresAt     = DateTime.UtcNow.AddSeconds(tokenJson.ExpiresIn);
        conn.ServiceUserId = profile?.Data?.Id ?? "";
        conn.DisplayName   = profile?.Data?.Attributes?.Username ?? "";
        conn.UpdatedAt     = DateTime.UtcNow;

        if (conn.Id == 0) db.UserConnections.Add(conn);
        await db.SaveChangesAsync();
        return conn;
    }

    // ── Token management ──────────────────────────────────────────────────────

    private async Task<string> GetAccessTokenAsync(string userId)
    {
        var conn = await db.UserConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Service == "tidal")
            ?? throw new InvalidOperationException("Tidal not connected");

        if (conn.ExpiresAt <= DateTime.UtcNow.AddMinutes(5))
        {
            var req = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "refresh_token",
                ["refresh_token"] = conn.RefreshToken,
                ["client_id"]     = _clientId,
            });
            var refreshMsg = new HttpRequestMessage(HttpMethod.Post, $"{AuthUrl}/oauth2/token")
            {
                Content = req
            };
            refreshMsg.Headers.Add("Referer", "https://login.tidal.com/");
            var resp = await http.SendAsync(refreshMsg);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                logger.LogError("Tidal token refresh failed {Status}: {Body}", resp.StatusCode, err);
                throw new Exception($"Tidal token refresh failed: {err}");
            }
            var tokens = await resp.Content.ReadFromJsonAsync<TidalTokenResponse>(JsonOpts)
                ?? throw new Exception("Failed to parse refresh response");
            conn.AccessToken = tokens.AccessToken;
            conn.ExpiresAt   = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn);
            conn.UpdatedAt   = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        return conn.AccessToken;
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    // Raw JSON:API GET — used for auth-time profile fetch where we have a raw token
    private async Task<T?> GetJsonApiAsync<T>(string token, string path)
    {
        var resp = await throttler.ExecuteAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}{path}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Accept", "application/vnd.api+json");
            return http.SendAsync(req);
        });
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            logger.LogWarning("Tidal {Path} → {Status}: {Body}", path, resp.StatusCode, body);
            return default;
        }
        return await resp.Content.ReadFromJsonAsync<T>(JsonOpts);
    }

    // Authenticated GET using stored token, with countryCode injected
    private async Task<T?> GetAsync<T>(string userId, string path, string? extraQuery = null)
    {
        var token     = await GetAccessTokenAsync(userId);
        var separator = path.Contains('?') ? "&" : "?";
        var fullPath  = $"{path}{separator}countryCode={_countryCode}{(extraQuery != null ? "&" + extraQuery : "")}";
        return await GetJsonApiAsync<T>(token, fullPath);
    }

    // Authenticated POST — countryCode injected, correct Content-Type for JSON:API
    private async Task<HttpResponseMessage> PostAsync(string userId, string path, object body)
    {
        var token     = await GetAccessTokenAsync(userId);
        var separator = path.Contains('?') ? "&" : "?";
        var fullPath  = $"{path}{separator}countryCode={_countryCode}";
        var bodyJson  = System.Text.Json.JsonSerializer.Serialize(body);
        logger.LogDebug("Tidal POST {Path} body: {Body}", fullPath, bodyJson);

        var resp = await throttler.ExecuteAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}{fullPath}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("Accept", "application/vnd.api+json");
            req.Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/vnd.api+json");
            return http.SendAsync(req);
        });
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            logger.LogError("Tidal POST {Path} → {Status}: {Body}", fullPath, resp.StatusCode, err);
        }
        return resp;
    }

    // ── Playlists ─────────────────────────────────────────────────────────────

    public async Task<List<PlaylistDto>> GetPlaylistsAsync(string userId, List<SyncMapping> mappings)
    {
        // GET /v2/playlists?countryCode=XX&filter[owners.id]=me&include=items
        var result = await GetAsync<TidalCollectionResponse<TidalPlaylistResource>>(
            userId, "/playlists", "filter%5Bowners.id%5D=me");

        if (result?.Data == null) return [];

        return result.Data.Select(p =>
        {
            var mapping = mappings.FirstOrDefault(m =>
                (m.SourceService == "tidal" && m.SourcePlaylistId == p.Id) ||
                (m.TargetService == "tidal" && m.TargetPlaylistId == p.Id));
            return new PlaylistDto(
                p.Id,
                p.Attributes?.Name ?? "",
                p.Attributes?.NumberOfItems ?? 0,
                p.Attributes?.Images?.FirstOrDefault()?.Url,
                mapping != null,
                mapping?.LastSyncedAt,
                mapping?.LastSyncStatus ?? "never"
            );
        }).ToList();
    }

    public async Task<List<TrackDto>> GetPlaylistTracksAsync(string userId, string playlistId)
    {
        // GET /v2/playlists/{id}/relationships/items?countryCode=XX&include=items
        var result = await GetAsync<TidalRelationshipResponse<TidalTrackResource>>(
            userId, $"/playlists/{playlistId}/relationships/items", "include=items");

        // Tracks come back in the top-level `included` array
        if (result?.Included == null) return [];

        return result.Included.Select(t => new TrackDto(
            t.Id,
            t.Attributes?.Title ?? "",
            t.Attributes?.ArtistName ?? "",
            t.Attributes?.Album?.Title,
            t.Attributes?.Isrc,
            t.Attributes?.Album?.ImageUrl,
            t.Attributes?.DurationSeconds * 1000 ?? 0
        )).ToList();
    }

    public async Task<string> CreatePlaylistAsync(string userId, string name)
    {
        var resp = await PostAsync(userId, $"/playlists?countryCode={_countryCode}", new
        {
            data = new
            {
                type       = "playlists",
                attributes = new { name, description = "Synced by PlaylistSync", privacy = "PRIVATE" }
            }
        });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TidalSingleResponse<TidalPlaylistResource>>(JsonOpts);
        return body!.Data!.Id;
    }

    public async Task AddTracksAsync(string userId, string playlistId, IEnumerable<string> tidalTrackIds)
    {
        foreach (var chunk in tidalTrackIds.Chunk(20))
        {
            var resp = await PostAsync(userId, $"/playlists/{playlistId}/relationships/items", new
            {
                data = chunk.Select(id => new { type = "tracks", id }).ToArray()
            });
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                throw new Exception($"Tidal AddTracks failed ({resp.StatusCode}): {err}");
            }
        }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    public async Task<TrackDto?> SearchTrackAsync(string userId, string name, string artist, string? isrc)
    {
        // ISRC lookup: GET /v2/tracks?countryCode=XX&filter[isrc]=XX&include=artists
        if (!string.IsNullOrEmpty(isrc))
        {
            var isrcResult = await GetAsync<TidalCollectionResponse<TidalTrackResource>>(
                userId, "/tracks", $"filter%5Bisrc%5D={Uri.EscapeDataString(isrc)}&include=artists");
            var isrcTrack = isrcResult?.Data?.FirstOrDefault();
            if (isrcTrack != null) return MapTrack(isrcTrack);
        }

        // Full-text search: GET /v2/searchresults/{query}?countryCode=XX&include=tracks
        var query  = Uri.EscapeDataString($"{name} {artist}");
        var result = await GetAsync<TidalSearchResponse>(
            userId, $"/searchresults/{query}", "include=tracks&limit=5");

        // Search returns track IDs in relationships; full data is in included[]
        var track = result?.Included?.FirstOrDefault();
        return track == null ? null : MapTrack(track);
    }

    private static TrackDto MapTrack(TidalTrackResource t) => new(
        t.Id,
        t.Attributes?.Title ?? "",
        t.Attributes?.ArtistName ?? "",
        t.Attributes?.Album?.Title,
        t.Attributes?.Isrc,
        t.Attributes?.Album?.ImageUrl,
        t.Attributes?.DurationSeconds * 1000 ?? 0);

    /// Fetches any public Tidal playlist by ID.
    /// Uses the user's token if they have Tidal connected; otherwise uses client credentials.
    public async Task<(PlaylistDto metadata, List<TrackDto> tracks)> GetPlaylistByIdAsync(string userId, string playlistId)
    {
        if (string.IsNullOrEmpty(userId))
            throw new InvalidOperationException("Must be logged into Tidal to import a Tidal playlist.");

        var token = await GetAccessTokenAsync(userId);

        var playlist = await GetJsonApiAsync<TidalSingleResponse<TidalPlaylistResource>>(
            token, $"/playlists/{playlistId}?countryCode={_countryCode}");

        var items = await GetJsonApiAsync<TidalRelationshipResponse<TidalTrackResource>>(
            token, $"/playlists/{playlistId}/relationships/items?countryCode={_countryCode}&include=items");

        var metadata = new PlaylistDto(
            playlistId,
            playlist?.Data?.Attributes?.Name ?? "Unknown Playlist",
            playlist?.Data?.Attributes?.NumberOfItems ?? 0,
            playlist?.Data?.Attributes?.Images?.FirstOrDefault()?.Url,
            false, null, "never");

        var tracks = items?.Included?.Select(t => new TrackDto(
            t.Id,
            t.Attributes?.Title ?? "",
            t.Attributes?.ArtistName ?? "",
            t.Attributes?.Album?.Title,
            t.Attributes?.Isrc,
            t.Attributes?.Album?.ImageUrl,
            t.Attributes?.DurationSeconds * 1000 ?? 0
        )).ToList() ?? [];

        return (metadata, tracks);
    }

    /// Gets an app-level token using client credentials — for public catalog access
    /// without a user login. Tidal supports this for read-only catalog endpoints.
    private async Task<string> GetClientCredentialsTokenAsync()
    {
        var clientSecret = _config["Tidal:ClientSecret"];
        if (string.IsNullOrEmpty(clientSecret))
            throw new InvalidOperationException(
                "Tidal client credentials not configured. " +
                "Set Tidal:ClientSecret in appsettings to enable public playlist import.");

        var req = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "client_credentials",
            ["client_id"]     = _clientId,
            ["client_secret"] = clientSecret,
        });
        var resp = await http.PostAsync($"{AuthUrl}/oauth2/token", req);
        resp.EnsureSuccessStatusCode();
        var token = await resp.Content.ReadFromJsonAsync<TidalTokenResponse>(JsonOpts)
            ?? throw new Exception("Failed to parse client credentials token");
        return token.AccessToken;
    }
}

// ── JSON:API response shapes ──────────────────────────────────────────────────

record TidalTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")]    int ExpiresIn
);

// { data: [...] }
record TidalCollectionResponse<T>(
    [property: JsonPropertyName("data")] List<T>? Data
);

// { data: T }
record TidalSingleResponse<T>(
    [property: JsonPropertyName("data")] T? Data
);

// { data: [...relationships...], included: [...full resources...] }
record TidalRelationshipResponse<T>(
    [property: JsonPropertyName("data")]     List<JsonElement>? Data,
    [property: JsonPropertyName("included")] List<T>? Included
);

// Search returns relationships.tracks.data + included[]
record TidalSearchResponse(
    [property: JsonPropertyName("included")] List<TidalTrackResource>? Included
);

// ── Resource types ────────────────────────────────────────────────────────────

record TidalPlaylistResource(
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("type")]       string Type,
    [property: JsonPropertyName("attributes")] TidalPlaylistAttributes? Attributes
);
record TidalPlaylistAttributes(
    [property: JsonPropertyName("name")]         string Name,
    [property: JsonPropertyName("numberOfItems")] int? NumberOfItems,
    [property: JsonPropertyName("images")]       List<TidalImage>? Images
);

record TidalTrackResource(
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("type")]       string Type,
    [property: JsonPropertyName("attributes")] TidalTrackAttributes? Attributes
);
record TidalTrackAttributes(
    [property: JsonPropertyName("title")]           string Title,
    [property: JsonPropertyName("artistName")]      string ArtistName,
    [property: JsonPropertyName("isrc")]            string? Isrc,
    [property: JsonPropertyName("durationSeconds")] int? DurationSeconds,
    [property: JsonPropertyName("album")]           TidalAlbumRef? Album
);
record TidalAlbumRef(
    [property: JsonPropertyName("title")]    string? Title,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl
);
record TidalImage(
    [property: JsonPropertyName("url")]    string Url,
    [property: JsonPropertyName("width")]  int Width,
    [property: JsonPropertyName("height")] int Height
);

// /users/me
record TidalUserResource(
    [property: JsonPropertyName("data")] TidalUserData? Data
);
record TidalUserData(
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("attributes")] TidalUserAttributes? Attributes
);
record TidalUserAttributes(
    [property: JsonPropertyName("username")] string Username
);
