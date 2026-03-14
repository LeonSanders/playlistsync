using Hangfire;
using PlaylistSync.Data;
using PlaylistSync.Services;
using Microsoft.EntityFrameworkCore;

namespace PlaylistSync.Jobs;

public class PlaylistSyncJob(SyncService syncService, AppDbContext db, ILogger<PlaylistSyncJob> logger)
{
    [AutomaticRetry(Attempts = 2)]
    public async Task RunAsync(int mappingId, string userId)
    {
        logger.LogInformation("Running scheduled sync for mapping {MappingId}", mappingId);
        await syncService.SyncMappingAsync(mappingId, userId, SyncTrigger.Scheduled);
    }

    // Called by the recurring job — iterates all auto-sync mappings
    public async Task RunAllPendingAsync()
    {
        var due = await db.SyncMappings
            .Where(m => m.AutoSync &&
                        (m.LastSyncedAt == null || m.LastSyncedAt < DateTime.UtcNow.AddHours(-1)))
            .ToListAsync();

        logger.LogInformation("Auto-sync: {Count} mappings due", due.Count);
        foreach (var mapping in due)
            BackgroundJob.Enqueue<PlaylistSyncJob>(j => j.RunAsync(mapping.Id, mapping.UserId));
    }
}
