using Microsoft.EntityFrameworkCore;

namespace PlaylistSync.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<UserConnection> UserConnections => Set<UserConnection>();
    public DbSet<SyncMapping> SyncMappings => Set<SyncMapping>();
    public DbSet<SyncLog> SyncLogs => Set<SyncLog>();
    public DbSet<OAuthState> OAuthStates => Set<OAuthState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserConnection>().HasIndex(u => new { u.UserId, u.Service }).IsUnique();
        b.Entity<SyncMapping>().HasIndex(m => new { m.UserId, m.SourceService, m.SourcePlaylistId }).IsUnique();
    }
}

public class UserConnection
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Service { get; set; } = "";         // "spotify" | "tidal"
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public string ServiceUserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SyncMapping
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string SourceService { get; set; } = "";   // "spotify" | "tidal"
    public string SourcePlaylistId { get; set; } = "";
    public string SourcePlaylistName { get; set; } = "";
    public string TargetService { get; set; } = "";
    public string TargetPlaylistId { get; set; } = "";
    public string TargetPlaylistName { get; set; } = "";
    public SyncDirection Direction { get; set; } = SyncDirection.Bidirectional;
    public bool AutoSync { get; set; } = true;
    public DateTime? LastSyncedAt { get; set; }
    public string LastSyncStatus { get; set; } = "never";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SyncLog
{
    public int Id { get; set; }
    public int MappingId { get; set; }
    public string UserId { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int TracksAdded { get; set; }
    public int TracksRemoved { get; set; }
    public int TracksSkipped { get; set; }
    public string? ErrorMessage { get; set; }
    public SyncTrigger Trigger { get; set; }
}

public enum SyncDirection { SourceToTarget, TargetToSource, Bidirectional }
public enum SyncTrigger { Manual, Scheduled, Webhook }

public class OAuthState
{
    public int Id { get; set; }
    public string State { get; set; } = "";       // the random value sent to the provider
    public string UserId { get; set; } = "";       // so we know who to attach the token to
    public string Service { get; set; } = "";      // "spotify" | "tidal"
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(10);
}
