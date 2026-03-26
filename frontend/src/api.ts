import type { ConnectionStatus, Playlist, TrackSyncStatus, Mapping, SyncResult, ImportedPlaylist } from './types'

const base = '/api'

async function req<T>(url: string, options?: RequestInit): Promise<T> {
  const storedUserId = localStorage.getItem('playlistsync_user_id')
  const headers: Record<string, string> = { ...(options?.headers as Record<string, string> ?? {}) }
  // Don't send X-User-Id on auth/status — the cookie must be authoritative there
  // so we always pick up the correct userId after OAuth redirects
  if (storedUserId && !url.includes('/auth/status')) headers['X-User-Id'] = storedUserId

  const res = await fetch(url, { credentials: 'include', ...options, headers })
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`)
  if (res.status === 204) return undefined as T
  const data = await res.json()
  // Always update localStorage from auth/status so it stays in sync with the cookie
  if (url.includes('/auth/status') && data.userId)
    localStorage.setItem('playlistsync_user_id', data.userId)
  return data
}

export const api = {
  auth: {
    status: () => req<ConnectionStatus>('/auth/status'),
    spotifyLogin: () => { window.location.href = '/auth/spotify/login' },
    tidalLogin: () => { window.location.href = '/auth/tidal/login' },
    disconnect: (service: string) => req<void>(`/auth/${service}`, { method: 'DELETE' }),
  },

  playlists: {
    getSpotify: () => req<Playlist[]>(`${base}/playlists/spotify`),
    getTidal: () => req<Playlist[]>(`${base}/playlists/tidal`),
    fromUrl: (url: string) => req<ImportedPlaylist>(`${base}/playlists/from-url`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ url }),
    }),
  },

  sync: {
    getMappings: () => req<Mapping[]>(`${base}/sync/mappings`),

    createMapping: (body: {
      sourceService: string
      sourcePlaylistId: string
      targetService: string
      targetPlaylistId: string
      direction: string
      autoSync: boolean
    }) => req<number>(`${base}/sync/mappings`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }),

    deleteMapping: (id: number) =>
      req<void>(`${base}/sync/mappings/${id}`, { method: 'DELETE' }),

    triggerSync: (id: number) =>
      req<SyncResult>(`${base}/sync/mappings/${id}/sync`, { method: 'POST' }),

    getStatus: (id: number) =>
      req<TrackSyncStatus[]>(`${base}/sync/mappings/${id}/status`),

    syncFromTracks: (body: {
      sourceTracks: import('./types').Track[]
      targetService: string
      targetPlaylistId: string
      targetPlaylistName: string
      direction: string
      sourceService?: string
      sourcePlaylistId?: string
    }) => req<import('./types').SyncResult>(`${base}/sync/from-tracks`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }),

    updateMapping: (id: number, patch: { autoSync?: boolean; direction?: string }) =>
      req<void>(`${base}/sync/mappings/${id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(patch),
      }),
  },
}
