using SpotifyAPI.Web;
using PlaylistSync.Data;
using PlaylistSync.DTOs;
using Microsoft.EntityFrameworkCore;

namespace PlaylistSync.Services;

public class SpotifyService(IConfiguration config, AppDbContext db, ILogger<SpotifyService> logger)
{
    private readonly string _clientId = config["Spotify:ClientId"]!;
    private readonly string _clientSecret = config["Spotify:ClientSecret"]!;
    private readonly string _redirectUri = config["Spotify:RedirectUri"]!;

    public string GetAuthorizationUrl(string state)
    {
        var request = new LoginRequest(
            new Uri(_redirectUri),
            _clientId,
            LoginRequest.ResponseType.Code)
        {
            Scope = new List<string> {
                Scopes.PlaylistReadPrivate, Scopes.PlaylistReadCollaborative,
                Scopes.PlaylistModifyPublic, Scopes.PlaylistModifyPrivate
            },
            State = state
        };
        return request.ToUri().ToString();
    }

    public async Task<UserConnection> HandleCallbackAsync(string code, string userId)
    {
        AuthorizationCodeTokenResponse response;
        try
        {
            response = await new OAuthClient().RequestToken(
                new AuthorizationCodeTokenRequest(_clientId, _clientSecret, code, new Uri(_redirectUri)));
        }
        catch (Exception ex)
        {
            throw new Exception($"Spotify token exchange failed — the authorization code may have expired or already been used. Please try connecting again. ({ex.Message})");
        }

        var spotifyClient = new SpotifyClient(response.AccessToken);

        // Profile fetch is best-effort — don't fail the whole auth if it errors
        string serviceUserId = "", displayName = "";
        try
        {
            var profile = await spotifyClient.UserProfile.Current();
            serviceUserId = profile.Id;
            displayName   = profile.DisplayName ?? profile.Id;
        }
        catch (Exception ex)
        {
            // Log but continue — token is still valid, profile fetch can fail
            // if the account is in dev mode and not allowlisted
        }

        var conn = await db.UserConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Service == "spotify")
            ?? new UserConnection { UserId = userId, Service = "spotify" };

        conn.AccessToken    = response.AccessToken;
        conn.RefreshToken   = response.RefreshToken;
        conn.ExpiresAt      = DateTime.UtcNow.AddSeconds(response.ExpiresIn);
        conn.ServiceUserId  = serviceUserId;
        conn.DisplayName    = displayName.Length > 0 ? displayName : "Spotify User";
        conn.UpdatedAt      = DateTime.UtcNow;

        if (conn.Id == 0) db.UserConnections.Add(conn);
        await db.SaveChangesAsync();
        return conn;
    }

    public async Task<SpotifyClient> GetClientAsync(string userId)
    {
        var conn = await db.UserConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Service == "spotify")
            ?? throw new InvalidOperationException("Spotify not connected");

        if (conn.ExpiresAt <= DateTime.UtcNow.AddMinutes(5))
        {
            var refreshed = await new OAuthClient().RequestToken(
                new AuthorizationCodeRefreshRequest(_clientId, _clientSecret, conn.RefreshToken));
            conn.AccessToken = refreshed.AccessToken;
            conn.ExpiresAt = DateTime.UtcNow.AddSeconds(refreshed.ExpiresIn);
            conn.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        return new SpotifyClient(conn.AccessToken);
    }

    // Returns a valid (refreshed if needed) access token for direct HTTP calls
    private async Task<string> GetFreshTokenAsync(string userId)
    {
        await GetClientAsync(userId); // triggers refresh if needed + saves
        var conn = await db.UserConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Service == "spotify")
            ?? throw new InvalidOperationException("Spotify not connected");
        return conn.AccessToken;
    }

    public async Task<List<PlaylistDto>> GetPlaylistsAsync(string userId, List<Data.SyncMapping> mappings)
    {
        try
        {
            var token = await GetFreshTokenAsync(userId);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var playlists = new List<PlaylistDto>();
            string? url = "https://api.spotify.com/v1/me/playlists?limit=50";
            while (url != null)
            {
                var resp = await http.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                var page = await resp.Content.ReadFromJsonAsync<SpotifyPlaylistsPage>(JsonOpts)!;
                foreach (var p in page!.Items ?? [])
                {
                    if (p.Id == null) continue;
                    var mapping = mappings.FirstOrDefault(m =>
                        (m.SourceService == "spotify" && m.SourcePlaylistId == p.Id) ||
                        (m.TargetService == "spotify" && m.TargetPlaylistId == p.Id));
                    playlists.Add(new PlaylistDto(p.Id, p.Name ?? "",
                        p.Tracks?.Total ?? 0,
                        p.Images?.FirstOrDefault()?.Url,
                        mapping != null, mapping?.LastSyncedAt, mapping?.LastSyncStatus ?? "never"));
                }
                url = page.Next;
            }
            return playlists;
        }
        catch (Exception)
        {
            // Token is invalid — remove the broken connection so the user can re-auth
            var conn = await db.UserConnections
                .FirstOrDefaultAsync(c => c.UserId == userId && c.Service == "spotify");
            if (conn != null) { db.UserConnections.Remove(conn); await db.SaveChangesAsync(); }
            return [];
        }
    }

    public async Task<List<TrackDto>> GetPlaylistTracksAsync(string userId, string playlistId)
    {
        // GET /playlists/{id}/items — replaces removed /playlists/{id}/tracks (Feb 2026)
        var token = await GetFreshTokenAsync(userId);
        logger.LogInformation("GetPlaylistTracksAsync {PlaylistId}, token length: {Len}", playlistId, token?.Length ?? 0);
        var tracks = new List<TrackDto>();
        string? url = $"https://api.spotify.com/v1/playlists/{playlistId}/items?limit=50";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        while (url != null)
        {
            var resp = await http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var page = await resp.Content.ReadFromJsonAsync<SpotifyItemsPage>(JsonOpts)!;
            foreach (var item in page!.Items ?? [])
            {
                if (item.Track is not { } t || t.Id == null) continue;
                var isrc = t.ExternalIds != null && t.ExternalIds.TryGetValue("isrc", out var isrcVal) ? isrcVal : null;
                tracks.Add(new TrackDto(t.Id, t.Name ?? "",
                    string.Join(", ", t.Artists?.Select(a => a.Name ?? "") ?? []),
                    t.Album?.Name, isrc,
                    t.Album?.Images?.FirstOrDefault()?.Url,
                    t.DurationMs));
            }
            url = page.Next;
        }
        return tracks;
    }

    public async Task<string> CreatePlaylistAsync(string userId, string name)
    {
        // POST /me/playlists — replaces removed POST /users/{id}/playlists (Feb 2026)
        var conn = await db.UserConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Service == "spotify")
            ?? throw new InvalidOperationException("Spotify not connected");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", conn.AccessToken);
        var resp = await http.PostAsJsonAsync("https://api.spotify.com/v1/me/playlists", new
        {
            name,
            description = "Synced by PlaylistSync",
            @public = false
        });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("id").GetString()!;
    }

    public async Task AddTracksAsync(string userId, string playlistId, IEnumerable<string> spotifyTrackIds)
    {
        // POST /playlists/{id}/items — replaces removed /playlists/{id}/tracks (Feb 2026)
        var token = await GetFreshTokenAsync(userId);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        foreach (var chunk in spotifyTrackIds.Chunk(100))
        {
            var uris = chunk.Select(id => $"spotify:track:{id}").ToList();
            var resp = await http.PostAsJsonAsync(
                $"https://api.spotify.com/v1/playlists/{playlistId}/items",
                new { uris });
            resp.EnsureSuccessStatusCode();
        }
    }

    public async Task RemoveTracksAsync(string userId, string playlistId, IEnumerable<string> spotifyTrackIds)
    {
        var client = await GetClientAsync(userId);
        var chunks = spotifyTrackIds.Chunk(100);
        foreach (var chunk in chunks)
        {
            var items = chunk.Select(id => new PlaylistRemoveItemsRequest.Item { Uri = $"spotify:track:{id}" }).ToList();
            await client.Playlists.RemoveItems(playlistId, new PlaylistRemoveItemsRequest { Tracks = items });
        }
    }

    public async Task<TrackDto?> SearchTrackAsync(string userId, string name, string artist, string? isrc)
    {
        var client = await GetClientAsync(userId);

        if (!string.IsNullOrEmpty(isrc))
        {
            var isrcResult = await client.Search.Item(
                new SearchRequest(SearchRequest.Types.Track, $"isrc:{isrc}"));
            var isrcTrack = isrcResult.Tracks.Items?.FirstOrDefault();
            if (isrcTrack != null)
                return new TrackDto(isrcTrack.Id, isrcTrack.Name,
                    string.Join(", ", isrcTrack.Artists.Select(a => a.Name)),
                    isrcTrack.Album?.Name, isrc,
                    isrcTrack.Album?.Images?.FirstOrDefault()?.Url, isrcTrack.DurationMs);
        }

        var query = $"track:{name} artist:{artist}";
        var result = await client.Search.Item(new SearchRequest(SearchRequest.Types.Track, query));
        var track = result.Tracks.Items?.FirstOrDefault();
        if (track == null) return null;

        return new TrackDto(track.Id, track.Name,
            string.Join(", ", track.Artists.Select(a => a.Name)),
            track.Album?.Name, null,
            track.Album?.Images?.FirstOrDefault()?.Url, track.DurationMs);
    }
    /// Fetches a playlist by ID — works even if it's not the user's own playlist.
    /// Falls back to app credentials if the user doesn't have Spotify connected.
    public async Task<(PlaylistDto metadata, List<TrackDto> tracks)> GetPlaylistByIdAsync(string userId, string playlistId)
    {
        string token;
        bool usingClientCredentials = false;
        try
        {
            if (string.IsNullOrEmpty(userId)) throw new InvalidOperationException("No user");
            token = await GetFreshTokenAsync(userId);
        }
        catch (Exception ex)
        {
            logger.LogWarning("GetPlaylistByIdAsync falling back to client credentials: {Reason}", ex.Message);
            usingClientCredentials = true;
            token = (await new OAuthClient().RequestToken(
                new ClientCredentialsRequest(_clientId, _clientSecret))).AccessToken;
        }
        logger.LogInformation("GetPlaylistByIdAsync {PlaylistId} using {Auth}", playlistId, usingClientCredentials ? "client_credentials" : "user_token");

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var plResp = await http.GetAsync($"https://api.spotify.com/v1/playlists/{playlistId}?fields=id,name,tracks.total");
            plResp.EnsureSuccessStatusCode();
            var pl = await plResp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var plName = pl.GetProperty("name").GetString() ?? "Unknown";
            var plTotal = pl.TryGetProperty("tracks", out var t) && t.TryGetProperty("total", out var tot) ? tot.GetInt32() : 0;

            var tracks = new List<TrackDto>();
            string? nextUrl = $"https://api.spotify.com/v1/playlists/{playlistId}/items?limit=50";
            while (nextUrl != null)
            {
                var tResp = await http.GetAsync(nextUrl);
                tResp.EnsureSuccessStatusCode();
                var page = await tResp.Content.ReadFromJsonAsync<SpotifyItemsPage>(JsonOpts)!;
                foreach (var item in page!.Items ?? [])
                {
                    if (item.Track is not { } track || track.Id == null) continue;
                    var isrc2 = track.ExternalIds != null && track.ExternalIds.TryGetValue("isrc", out var isrcVal) ? isrcVal : null;
                    tracks.Add(new TrackDto(track.Id, track.Name ?? "",
                        string.Join(", ", track.Artists?.Select(a => a.Name ?? "") ?? []),
                        track.Album?.Name, isrc2,
                        track.Album?.Images?.FirstOrDefault()?.Url,
                        track.DurationMs));
                }
                nextUrl = page.Next;
            }

            var metadata = new PlaylistDto(playlistId, plName, plTotal,
                null, false, null, "never");
            return (metadata, tracks);
        }
        catch (HttpRequestException ex)
        {
            var friendly = ex.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "This playlist is private or not accessible. Make sure it's set to Public in Spotify."
                : $"Spotify API error for playlist {playlistId}: {ex.Message}";
            throw new Exception(friendly);
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

// ── Spotify HTTP response shapes ──────────────────────────────────────────────
record SpotifyItemsPage(
    [property: System.Text.Json.Serialization.JsonPropertyName("items")] List<SpotifyPlaylistItem>? Items,
    [property: System.Text.Json.Serialization.JsonPropertyName("next")]  string? Next
);
record SpotifyPlaylistItem(
    [property: System.Text.Json.Serialization.JsonPropertyName("track")] SpotifyTrackItem? Track
);
record SpotifyTrackItem(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")]           string? Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")]         string? Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("duration_ms")]  int DurationMs,
    [property: System.Text.Json.Serialization.JsonPropertyName("artists")]      List<SpotifyArtistItem>? Artists,
    [property: System.Text.Json.Serialization.JsonPropertyName("album")]        SpotifyAlbumItem? Album,
    [property: System.Text.Json.Serialization.JsonPropertyName("external_ids")] Dictionary<string, string>? ExternalIds
);
record SpotifyArtistItem([property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name);
record SpotifyAlbumItem(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")]   string? Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("images")] List<SpotifyImageItem>? Images
);
record SpotifyImageItem([property: System.Text.Json.Serialization.JsonPropertyName("url")] string? Url);

record SpotifyPlaylistsPage(
    [property: System.Text.Json.Serialization.JsonPropertyName("items")] List<SpotifyPlaylistItem2>? Items,
    [property: System.Text.Json.Serialization.JsonPropertyName("next")]  string? Next
);
record SpotifyPlaylistItem2(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")]     string? Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")]   string? Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("images")] List<SpotifyImageItem>? Images,
    [property: System.Text.Json.Serialization.JsonPropertyName("tracks")] SpotifyTrackCount? Tracks
);
record SpotifyTrackCount([property: System.Text.Json.Serialization.JsonPropertyName("total")] int Total);
