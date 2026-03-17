using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using PlaylistSync.Data;
using PlaylistSync.DTOs;
using PlaylistSync.Services;

namespace PlaylistSync.Tests;

/// <summary>
/// Boots the full ASP.NET Core pipeline against an in-memory SQLite database.
/// SpotifyService and TidalService are replaced with NSubstitute mocks so no
/// real HTTP calls are made.
/// </summary>
public class PlaylistSyncFactory : WebApplicationFactory<Program>
{
    public SpotifyService SpotifyMock { get; } = Substitute.For<SpotifyService>();
    public TidalService TidalMock { get; } = Substitute.For<TidalService>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureTestServices(services =>
        {
            // Swap Postgres for in-memory SQLite
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(o =>
                o.UseInMemoryDatabase($"playlistsync-test-{Guid.NewGuid()}"));

            // Replace real service implementations with mocks
            services.RemoveAll<SpotifyService>();
            services.RemoveAll<TidalService>();
            services.AddSingleton(SpotifyMock);
            services.AddSingleton(TidalMock);

            // Use in-memory Hangfire so jobs don't actually run during tests
            services.RemoveAll<IGlobalConfiguration>();
            services.AddHangfire(c => c.UseInMemoryStorage());
        });
    }

    /// <summary>Creates an HttpClient with a pre-set user_id cookie.</summary>
    public HttpClient CreateAuthenticatedClient(string userId = "test-user-123")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("Cookie", $"user_id={userId}");
        return client;
    }

    /// <summary>Resolves a scoped service directly (for seeding data etc.).</summary>
    public T GetService<T>() where T : notnull
    {
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>Seeds the in-memory DB and returns it.</summary>
    public AppDbContext GetDb()
    {
        var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        return db;
    }
}

/// <summary>Shared factory instance across all tests in the collection.</summary>
[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<PlaylistSyncFactory> { }
