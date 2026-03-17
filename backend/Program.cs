using Hangfire;
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

builder.Services.AddHangfire(c => c
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(isTest ? "Host=localhost" : pg)));
builder.Services.AddHangfireServer();

builder.Services.AddHttpClient<TidalService>();
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
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(frontendUrl).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

if (!isTest)
{
    using var scope = app.Services.CreateScope();
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

if (!isTest)
    RecurringJob.AddOrUpdate<PlaylistSyncJob>(
        "auto-sync-all",
        j => j.RunAllPendingAsync(),
        Cron.Hourly);

app.Run();

// Expose Program class for WebApplicationFactory in tests
public partial class Program { }

