using Hangfire;
using Hangfire.InMemory;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using PlaylistSync.Data;
using PlaylistSync.Jobs;
using PlaylistSync.Services;

var builder = WebApplication.CreateBuilder(args);

var isTest = builder.Environment.EnvironmentName == "Test";
var pg = builder.Configuration.GetConnectionString("Postgres") ?? "";

if (!isTest)
    builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(pg));

if (!isTest)
{
    builder.Services.AddHangfire(c => c
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(pg)));
    builder.Services.AddHangfireServer();
}
else
{
    builder.Services.AddHangfire(c => c.UseInMemoryStorage());
    builder.Services.AddHangfireServer();
}

builder.Services.AddHttpClient<TidalService>(c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
    c.DefaultRequestHeaders.Add("Origin", "https://login.tidal.com");
});
builder.Services.AddSingleton<TidalThrottler>();
if (!isTest)
{
    builder.Services.AddScoped<SpotifyService>();
    builder.Services.AddScoped<TidalService>();
}
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<PlaylistSyncJob>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var frontendUrl = builder.Configuration["Frontend:Url"] ?? "http://localhost:5173";
// CORS only needed in dev (when frontend runs on a different port)
if (!builder.Environment.IsProduction())
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.WithOrigins(frontendUrl).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

if (!isTest)
{
    using var scope = app.Services.CreateScope();
    var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var retries = 10;
    while (retries-- > 0)
    {
        try { await ctx.Database.MigrateAsync(); break; }
        catch (Exception ex) when (retries > 0)
        {
            logger.LogWarning("Postgres not ready, retrying in 2s... ({Retries} left). {Message}", retries, ex.Message);
            await Task.Delay(2000);
        }
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsProduction())
    app.UseCors();

// Serve React from wwwroot in production
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();
app.MapHangfireDashboard("/hangfire");

// SPA fallback — return index.html for any non-API route so React Router works
app.MapFallbackToFile("index.html");

if (!isTest)
    RecurringJob.AddOrUpdate<PlaylistSyncJob>(
        "auto-sync-all",
        j => j.RunAllPendingAsync(),
        Cron.Hourly);

app.Run();

// Expose Program class for WebApplicationFactory in tests
public partial class Program { }

