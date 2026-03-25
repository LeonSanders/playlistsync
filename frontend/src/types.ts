export interface ConnectionStatus {
  userId: string
  spotify: { connected: boolean; displayName?: string }
  tidal: { connected: boolean; displayName?: string }
}

export interface Playlist {
  id: string
  name: string
  trackCount: number
  imageUrl?: string
  isMapped: boolean
  lastSyncedAt?: string
  syncStatus: string
}

export interface Track {
  id: string
  name: string
  artist: string
  album?: string
  isrc?: string
  imageUrl?: string
  durationMs: number
}

export interface TrackSyncStatus {
  track: Track
  status: 'synced' | 'missing' | 'new'
}

export interface Mapping {
  id: number
  sourceService: string
  sourcePlaylistId: string
  sourcePlaylistName: string
  targetService: string
  targetPlaylistId: string
  targetPlaylistName: string
  direction: string
  autoSync: boolean
  lastSyncedAt?: string
  lastSyncStatus: string
}

export interface SyncResult {
  success: boolean
  tracksAdded: number
  tracksRemoved: number
  tracksSkipped: number
  unmatchedCount: number
  unmatched: { name: string; artist: string; sourceService: string }[]
  error?: string
}

export interface ImportedPlaylist {
  service: string
  playlistId: string
  name: string
  trackCount: number
  imageUrl?: string
  tracks: Track[]
}
