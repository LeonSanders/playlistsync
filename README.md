# Playlistsync

Bidirectional playlist sync between Spotify and Tidal, with automatic hourly polling, manual sync, and Spotify webhooks.

## Stack

| Layer | Tech |
|-------|------|
| Backend | ASP.NET Core 8, C# |
| Database | PostgreSQL 16 (via EF Core + Npgsql) |
| Job queue | Hangfire (backed by Postgres — no Redis needed) |
| Spotify SDK | SpotifyAPI-NET |
| Tidal | REST (official Tidal Open API) |
| Frontend | React 18 + Vite + TypeScript |
| Serving | Nginx (reverse proxies /api and /auth to backend) |
| Deploy | Docker Compose |

---

## Quick start

### 1. Get API credentials

**Spotify**
1. Go to https://developer.spotify.com/dashboard
2. Create an app — any name is fine
3. Add a Redirect URI: `http://localhost/auth/spotify/callback`
4. Copy the Client ID and Client Secret

**Tidal**
1. Go to https://developer.tidal.com
2. Create an app and request API access (may take a day or two)
3. Add a Redirect URI: `http://localhost/auth/tidal/callback`
4. Copy the Client ID and Client Secret

### 2. Configure environment

```bash
cp .env.example .env
# Edit .env with your credentials
```

### 3. Run

```bash
docker compose up --build
```

The app will be available at http://localhost.

On first boot, EF Core automatically runs migrations and creates the database schema.

---

## Development (without Docker)

**Backend**
```bash
cd backend

# Set secrets via user-secrets (keeps credentials out of appsettings.json)
dotnet user-secrets set "Spotify:ClientId" "your_id"
dotnet user-secrets set "Spotify:ClientSecret" "your_secret"
dotnet user-secrets set "Tidal:ClientId" "your_id"
dotnet user-secrets set "Tidal:ClientSecret" "your_secret"

# You need a local Postgres instance — update ConnectionStrings:Postgres in appsettings.json
dotnet run
# Runs on http://localhost:5000
```

**Frontend**
```bash
cd frontend
npm install
npm run dev
# Runs on http://localhost:5173
# Vite proxies /api and /auth → localhost:5000
```

---

## Architecture

```
Browser (React)
    │  REST + cookies
    ▼
ASP.NET Core API  ──────────────────────┐
    │                                   │
    ├── /auth/*          OAuth flows    │
    ├── /api/playlists   List playlists │
    └── /api/sync        Mappings + sync│
             │                          │
             ▼                          ▼
         Hangfire              Spotify / Tidal APIs
       (Postgres-backed)
             │
             └── Recurring job: every hour
                 → finds mappings due for sync
                 → enqueues background jobs
```

### Sync logic

Track matching uses a two-pass strategy:

1. **ISRC match** — most tracks have an ISRC code (the universal track ID). If both services return an ISRC, it's used for exact matching across catalogs.
2. **Title + artist search** — for tracks without an ISRC, the sync searches the target service by title and artist name. This handles the majority of the remaining cases; the few that still fail are reported as "not found" in the UI.

For bidirectional sync, the engine diffs both sides by ISRC and adds each side's exclusives to the other. It does not delete tracks — only additive changes are made. This avoids accidental data loss if one side temporarily returns an empty result.

### Sync triggers

| Trigger | Mechanism |
|---------|-----------|
| Manual | POST /api/sync/mappings/{id}/sync |
| Scheduled | Hangfire recurring job, runs hourly |
| Webhook | POST /api/sync/webhook/spotify (Spotify sends playlist change events) |

### Hangfire dashboard

Available at http://localhost:5000/hangfire — shows job history, retries, and the recurring job schedule.

---

## Production deployment

For a real server, update the redirect URIs and `FRONTEND_URL` in `.env` to your public domain:

```env
SPOTIFY_REDIRECT_URI=https://yourdomain.com/auth/spotify/callback
TIDAL_REDIRECT_URI=https://yourdomain.com/auth/tidal/callback
FRONTEND_URL=https://yourdomain.com
```

Then add HTTPS termination — either via a separate Nginx/Caddy reverse proxy on the host, or by updating the frontend `nginx.conf` with SSL configuration.

---

## Adding more services (Deezer, YT Music, etc.)

The architecture is designed for this:

1. Create a new service class in `backend/Services/` implementing the same interface pattern as `SpotifyService` and `TidalService` (get playlists, get tracks, create playlist, add tracks, search track).
2. Add OAuth endpoints to `AuthController`.
3. Extend `SyncService` to route by the new service name string.
4. Add a new pill + playlist panel to the React frontend.

The sync engine itself doesn't need to change — it operates on `TrackDto` objects and calls the service layer by name.
