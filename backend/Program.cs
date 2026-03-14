using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using PlaylistSync.Data;
using PlaylistSync.Jobs;
using PlaylistSync.Services;

var builder = WebApplication.CreateBuilder(args);

var pg = builder.Configuration.GetConnectionString("Postgres")!;

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(pg));

builder.Services.AddHangfire(c => c
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(pg)));
builder.Services.AddHangfireServer();

builder.Services.AddHttpClient<TidalService>();
builder.Services.AddScoped<SpotifyService>();
builder.Services.AddScoped<TidalService>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<PlaylistSyncJob>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var frontendUrl = builder.Configuration["Frontend:Url"] ?? "http://localhost:5173";
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(frontendUrl).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await ctx.Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.MapControllers();
app.MapHangfireDashboard("/hangfire");

// Register the hourly auto-sync recurring job
RecurringJob.AddOrUpdate<PlaylistSyncJob>(
    "auto-sync-all",
    j => j.RunAllPendingAsync(),
    Cron.Hourly);

app.Run();
