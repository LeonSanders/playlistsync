using SpotifyAPI.Web;
using PlaylistSync.Data;
using PlaylistSync.DTOs;
using Microsoft.EntityFrameworkCore;

namespace PlaylistSync.Services;

public class SpotifyService(IConfiguration config, AppDbContext db)
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
        var response = await new OAuthClient().RequestToken(
            new AuthorizationCodeTokenRequest(_clientId, _clientSecret, code, new Uri(_redirectUri)));

        var spotify = new SpotifyClient(response.AccessToken);
        var profile = await spotify.UserProfile.Current();

        var conn = await db.UserConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Service == "spotify")
            ?? new UserConnection { UserId = userId, Service = "spotify" };

        conn.AccessToken = response.AccessToken;
        conn.RefreshToken = response.RefreshToken;
        conn.ExpiresAt = DateTime.UtcNow.AddSeconds(response.ExpiresIn);
        conn.ServiceUserId = profile.Id;
        conn.DisplayName = profile.DisplayName ?? profile.Id;
        conn.UpdatedAt = DateTime.UtcNow;

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

    public async Task<List<PlaylistDto>> GetPlaylistsAsync(string userId, List<Data.SyncMapping> mappings)
    {
        var client = await GetClientAsync(userId);
        var pages = await client.PaginateAll(await client.Playlists.CurrentUsers());

        return pages.Select(p =>
        {
            var mapping = mappings.FirstOrDefault(m =>
                (m.SourceService == "spotify" && m.SourcePlaylistId == p.Id) ||
                (m.TargetService == "spotify" && m.TargetPlaylistId == p.Id));
            return new PlaylistDto(
                p.Id!,
                p.Name!,
                p.Tracks?.Total ?? 0,
                p.Images?.FirstOrDefault()?.Url,
                mapping != null,
                mapping?.LastSyncedAt,
                mapping?.LastSyncStatus ?? "never"
            );
        }).ToList();
    }

    public async Task<List<TrackDto>> GetPlaylistTracksAsync(string userId, string playlistId)
    {
        var client = await GetClientAsync(userId);
        var pages = await client.PaginateAll(
            await client.Playlists.GetItems(playlistId));

        return pages
            .Where(i => i.Track is FullTrack)
            .Select(i =>
            {
                var t = (FullTrack)i.Track;
                return new TrackDto(
                    t.Id,
                    t.Name,
                    string.Join(", ", t.Artists.Select(a => a.Name)),
                    t.Album?.Name,
                    t.ExternalIds?.TryGetValue("isrc", out var isrc) == true ? isrc : null,
                    t.Album?.Images?.FirstOrDefault()?.Url,
                    t.DurationMs
                );
            }).ToList();
    }

    public async Task<string> CreatePlaylistAsync(string userId, string name)
    {
        var client = await GetClientAsync(userId);
        var profile = await client.UserProfile.Current();
        var playlist = await client.Playlists.Create(profile.Id,
            new PlaylistCreateRequest(name) { Description = "Synced by PlaylistSync" });
        return playlist.Id!;
    }

    public async Task AddTracksAsync(string userId, string playlistId, IEnumerable<string> spotifyTrackIds)
    {
        var client = await GetClientAsync(userId);
        var chunks = spotifyTrackIds.Chunk(100);
        foreach (var chunk in chunks)
        {
            var uris = chunk.Select(id => $"spotify:track:{id}").ToList();
            await client.Playlists.AddItems(playlistId, new PlaylistAddItemsRequest(uris));
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
}
