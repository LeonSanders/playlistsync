import { useState, useEffect, useCallback } from 'react'
import { api } from './api'
import type { ConnectionStatus, Playlist, TrackSyncStatus, Mapping, SyncResult, ImportedPlaylist } from './types'
import './app.css'

type SyncDir = 'bidirectional' | 'sourceToTarget' | 'targetToSource'

// Which service is on the left is determined by which connected first,
// or by the user swapping via the direction button.
type PanelLayout = { left: 'spotify' | 'tidal'; right: 'spotify' | 'tidal' }

export default function App() {
  const [status, setStatus]                   = useState<ConnectionStatus | null>(null)
  const [spotifyPlaylists, setSpotifyPlaylists] = useState<Playlist[]>([])
  const [tidalPlaylists, setTidalPlaylists]   = useState<Playlist[]>([])
  const [mappings, setMappings]               = useState<Mapping[]>([])
  const [layout, setLayout]                   = useState<PanelLayout>({ left: 'spotify', right: 'tidal' })
  const [selectedLeft, setSelectedLeft]       = useState<Playlist | null>(null)
  const [selectedRight, setSelectedRight]     = useState<Playlist | null>(null)
  const [importedPlaylist, setImportedPlaylist] = useState<ImportedPlaylist | null>(null)
  const [urlInput, setUrlInput]               = useState('')
  const [urlLoading, setUrlLoading]           = useState(false)
  const [trackStatus, setTrackStatus]         = useState<TrackSyncStatus[]>([])
  const [syncing, setSyncing]                 = useState(false)
  const [syncResult, setSyncResult]           = useState<SyncResult | null>(null)
  const [syncDir, setSyncDir]                 = useState<SyncDir>('bidirectional')
  const [autoSync, setAutoSync]               = useState(true)
  const [searchLeft, setSearchLeft]           = useState('')
  const [searchRight, setSearchRight]         = useState('')
  const [activeMapping, setActiveMapping]     = useState<Mapping | null>(null)
  const [toast, setToast]                     = useState<{ msg: string; type: 'success' | 'error' } | null>(null)

  const showToast = (msg: string, type: 'success' | 'error' = 'success') => {
    setToast({ msg, type })
    setTimeout(() => setToast(null), 3500)
  }

  const loadAll = useCallback(async () => {
    try {
      const s = await api.auth.status()
      setStatus(s)
      const m = await api.sync.getMappings()
      setMappings(m)

      // Dynamic layout: if only Tidal connected, put it on the left
      if (s.tidal.connected && !s.spotify.connected)
        setLayout({ left: 'tidal', right: 'spotify' })
      else
        setLayout({ left: 'spotify', right: 'tidal' })

      if (s.spotify.connected) setSpotifyPlaylists(await api.playlists.getSpotify())
      if (s.tidal.connected)   setTidalPlaylists(await api.playlists.getTidal())
    } catch (e) { console.error(e) }
  }, [])

  useEffect(() => { loadAll() }, [loadAll])

  useEffect(() => {
    const params = new URLSearchParams(window.location.search)
    if (params.has('spotify')) { showToast('Spotify connected!'); window.history.replaceState({}, '', '/') }
    if (params.has('tidal'))   { showToast('Tidal connected!');   window.history.replaceState({}, '', '/') }
  }, [])

  const leftPlaylists  = layout.left  === 'spotify' ? spotifyPlaylists : tidalPlaylists
  const rightPlaylists = layout.right === 'spotify' ? spotifyPlaylists : tidalPlaylists

  const selectLeft = async (pl: Playlist) => {
    setSelectedLeft(pl)
    setSyncResult(null)
    setTrackStatus([])
    setImportedPlaylist(null)
    const existing = mappings.find(m =>
      m.sourcePlaylistId === pl.id || m.targetPlaylistId === pl.id)
    setActiveMapping(existing ?? null)
    if (existing) {
      const s = await api.sync.getStatus(existing.id)
      setTrackStatus(s)
      const rightId = layout.left === 'spotify' ? existing.targetPlaylistId : existing.sourcePlaylistId
      setSelectedRight(rightPlaylists.find(p => p.id === rightId) ?? null)
    }
  }

  const handleImportUrl = async () => {
    if (!urlInput.trim()) return
    setUrlLoading(true)
    try {
      const result = await api.playlists.fromUrl(urlInput.trim())
      setImportedPlaylist(result)
      setUrlInput('')
      showToast(`Loaded "${result.name}" — ${result.trackCount} tracks`)
    } catch (e: any) {
      showToast(e.message, 'error')
    } finally { setUrlLoading(false) }
  }

  const handleSync = async () => {
    if (!activeMapping) return
    setSyncing(true)
    setSyncResult(null)
    try {
      const result = await api.sync.triggerSync(activeMapping.id)
      setSyncResult(result)
      if (result.success) {
        showToast(`Synced: +${result.tracksAdded} tracks${result.tracksSkipped ? `, ${result.tracksSkipped} skipped` : ''}`)
        const s = await api.sync.getStatus(activeMapping.id)
        setTrackStatus(s)
        await loadAll()
      } else showToast(result.error ?? 'Sync failed', 'error')
    } catch (e: any) { showToast(e.message, 'error') }
    finally { setSyncing(false) }
  }

  const handleDeleteMapping = async () => {
    if (!activeMapping) return
    await api.sync.deleteMapping(activeMapping.id)
    setActiveMapping(null)
    setTrackStatus([])
    await loadAll()
    showToast('Mapping removed')
  }

  const swapPanels = () => setLayout(l => ({ left: l.right, right: l.left }))

  const filteredLeft  = leftPlaylists.filter(p => p.name.toLowerCase().includes(searchLeft.toLowerCase()))
  const filteredRight = rightPlaylists.filter(p => p.name.toLowerCase().includes(searchRight.toLowerCase()))

  const synced    = trackStatus.filter(t => t.status === 'synced').length
  const missing   = trackStatus.filter(t => t.status === 'missing').length
  const newTracks = trackStatus.filter(t => t.status === 'new').length

  const leftConnected  = status?.[layout.left]?.connected ?? false
  const rightConnected = status?.[layout.right]?.connected ?? false
  const leftName       = status?.[layout.left]?.displayName
  const rightName      = status?.[layout.right]?.displayName

  return (
    <div className="app">
      {toast && <div className={`toast toast-${toast.type}`}>{toast.msg}</div>}

      <header className="topbar">
        <span className="logo">
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/>
          </svg>
          playlistsync
        </span>
        <div className="auth-pills">
          {status ? (
            <>
              <ServicePill service="spotify" connected={status.spotify.connected}
                name={status.spotify.displayName}
                onConnect={api.auth.spotifyLogin}
                onDisconnect={() => api.auth.disconnect('spotify').then(loadAll)} />
              <ServicePill service="tidal" connected={status.tidal.connected}
                name={status.tidal.displayName}
                onConnect={api.auth.tidalLogin}
                onDisconnect={() => api.auth.disconnect('tidal').then(loadAll)} />
            </>
          ) : <span className="loading-text">Connecting…</span>}
        </div>
      </header>

      {/* URL import bar */}
      <div className="url-bar">
        <input
          className="url-input"
          placeholder="Paste a Spotify or Tidal playlist URL to import without logging in…"
          value={urlInput}
          onChange={e => setUrlInput(e.target.value)}
          onKeyDown={e => e.key === 'Enter' && handleImportUrl()}
        />
        <button className="btn btn-primary" onClick={handleImportUrl} disabled={urlLoading || !urlInput.trim()}>
          {urlLoading ? 'Loading…' : 'Import'}
        </button>
      </div>

      <div className="main">
        {/* Left panel */}
        <div className="panel panel-source">
          <div className="panel-header">
            <div className="panel-meta">
              <span className="panel-label">Source</span>
              <span className="service-name">
                <span className={`dot dot-${layout.left}`} />
                {layout.left.charAt(0).toUpperCase() + layout.left.slice(1)}
              </span>
            </div>
            <input className="search-input" placeholder="Search playlists…"
              value={searchLeft} onChange={e => setSearchLeft(e.target.value)} />
          </div>
          <div className="playlist-list">
            {importedPlaylist && importedPlaylist.service === layout.left ? (
              <ImportedPlaylistCard playlist={importedPlaylist} onClear={() => setImportedPlaylist(null)} />
            ) : !leftConnected ? (
              <EmptyState
                msg={`Connect ${layout.left.charAt(0).toUpperCase() + layout.left.slice(1)} to see your playlists`}
                action={`Connect ${layout.left.charAt(0).toUpperCase() + layout.left.slice(1)}`}
                onAction={layout.left === 'spotify' ? api.auth.spotifyLogin : api.auth.tidalLogin} />
            ) : filteredLeft.map(p => (
              <PlaylistRow key={p.id} playlist={p}
                selected={selectedLeft?.id === p.id}
                onSelect={() => selectLeft(p)}
                accent={layout.left} />
            ))}
          </div>
        </div>

        {/* Middle controls */}
        <div className="middle-col">
          <button className="dir-btn" onClick={swapPanels} title="Swap panels">⇄</button>
          <button
            className={`sync-orb ${syncing ? 'syncing' : ''} ${!activeMapping ? 'disabled' : ''}`}
            onClick={handleSync} disabled={!activeMapping || syncing} title="Sync now">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8"/>
              <path d="M21 3v5h-5"/>
              <path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16"/>
              <path d="M8 16H3v5"/>
            </svg>
          </button>
          <button className={`auto-btn ${autoSync ? 'auto-on' : ''}`}
            onClick={() => setAutoSync(!autoSync)} title="Toggle auto-sync">
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/>
            </svg>
          </button>
        </div>

        {/* Right panel */}
        <div className="panel panel-target">
          <div className="panel-header">
            <div className="panel-meta">
              <span className="panel-label">Target</span>
              <span className="service-name">
                <span className={`dot dot-${layout.right}`} />
                {layout.right.charAt(0).toUpperCase() + layout.right.slice(1)}
              </span>
            </div>
            {activeMapping ? (
              <div className="mapping-bar">
                <span className="map-chip map-chip-sp">{selectedLeft?.name}</span>
                <span className="map-arrow">↔</span>
                <span className="map-chip map-chip-ti">{selectedRight?.name}</span>
                <button className="unlink-btn" onClick={handleDeleteMapping} title="Remove mapping">✕</button>
              </div>
            ) : (
              <div className="mapping-bar">
                <span className="hint-text">
                  {selectedLeft ? '← Pick a target playlist' : '← Select a source first'}
                </span>
              </div>
            )}
          </div>

          {trackStatus.length > 0 ? (
            <div className="track-list">
              <div className="track-stats">
                <span className="stat stat-synced">{synced} synced</span>
                {missing   > 0 && <span className="stat stat-missing">{missing} not found</span>}
                {newTracks > 0 && <span className="stat stat-new">{newTracks} new</span>}
              </div>
              {trackStatus.map((ts, i) => (
                <div key={ts.track.id} className="track-item">
                  <span className="track-num">{i + 1}</span>
                  <div className="track-art" />
                  <div className="track-info">
                    <div className="track-name">{ts.track.name}</div>
                    <div className="track-artist">{ts.track.artist}</div>
                  </div>
                  <span className={`track-badge badge-${ts.status}`}>
                    {ts.status === 'synced' ? 'synced' : ts.status === 'missing' ? 'not found' : 'new'}
                  </span>
                </div>
              ))}
            </div>
          ) : importedPlaylist && importedPlaylist.service === layout.right ? (
            <ImportedPlaylistCard playlist={importedPlaylist} onClear={() => setImportedPlaylist(null)} />
          ) : (
            <div className="playlist-list">
              {!rightConnected ? (
                <EmptyState
                  msg={`Connect ${layout.right.charAt(0).toUpperCase() + layout.right.slice(1)} — or paste a URL above`}
                  action={`Connect ${layout.right.charAt(0).toUpperCase() + layout.right.slice(1)}`}
                  onAction={layout.right === 'spotify' ? api.auth.spotifyLogin : api.auth.tidalLogin} />
              ) : filteredRight.map(p => (
                <PlaylistRow key={p.id} playlist={p}
                  selected={selectedRight?.id === p.id}
                  onSelect={() => setSelectedRight(p)}
                  accent={layout.right} />
              ))}
            </div>
          )}
        </div>
      </div>

      <footer className="bottombar">
        <span className="sync-meta">
          {activeMapping?.lastSyncedAt
            ? `Last synced ${new Date(activeMapping.lastSyncedAt).toLocaleString()}`
            : activeMapping ? 'Never synced' : 'No mapping selected'}
          {activeMapping?.autoSync && ' · auto-polling hourly'}
        </span>
        {syncResult && syncResult.unmatchedCount > 0 && (
          <span className="unmatched-note">{syncResult.unmatchedCount} tracks couldn't be matched</span>
        )}
        <button className="btn btn-primary" onClick={handleSync} disabled={!activeMapping || syncing}>
          {syncing ? 'Syncing…' : 'Sync now'}
        </button>
      </footer>
    </div>
  )
}

// ── Sub-components ────────────────────────────────────────────────────────────

function ServicePill({ service, connected, name, onConnect, onDisconnect }:
  { service: string; connected: boolean; name?: string; onConnect: () => void; onDisconnect: () => void }) {
  return connected ? (
    <div className="pill pill-connected" onClick={onDisconnect} title="Click to disconnect">
      <span className={`dot dot-${service}`} />{name ?? service}
    </div>
  ) : (
    <div className="pill pill-disconnected" onClick={onConnect}>
      <span className="dot dot-off" />Connect {service}
    </div>
  )
}

function PlaylistRow({ playlist, selected, onSelect, accent }:
  { playlist: Playlist; selected: boolean; onSelect: () => void; accent: string }) {
  return (
    <div className={`playlist-row ${selected ? `selected selected-${accent}` : ''}`} onClick={onSelect}>
      <div className="pl-art">{playlist.name[0]}</div>
      <div className="pl-info">
        <div className="pl-name">{playlist.name}</div>
        <div className="pl-meta">{playlist.trackCount} tracks</div>
      </div>
      <div className={`sync-dot ${playlist.isMapped ? 'sync-dot-on' : 'sync-dot-off'}`} />
    </div>
  )
}

function ImportedPlaylistCard({ playlist, onClear }:
  { playlist: ImportedPlaylist; onClear: () => void }) {
  return (
    <div className="imported-card">
      <div className="imported-header">
        <div className="pl-art">{playlist.name[0]}</div>
        <div className="pl-info">
          <div className="pl-name">{playlist.name}</div>
          <div className="pl-meta">{playlist.trackCount} tracks · imported from URL</div>
        </div>
        <button className="unlink-btn" onClick={onClear}>✕</button>
      </div>
      <div className="imported-tracks">
        {playlist.tracks.slice(0, 5).map((t, i) => (
          <div key={t.id} className="track-item">
            <span className="track-num">{i + 1}</span>
            <div className="track-art" />
            <div className="track-info">
              <div className="track-name">{t.name}</div>
              <div className="track-artist">{t.artist}</div>
            </div>
          </div>
        ))}
        {playlist.tracks.length > 5 && (
          <div className="track-item" style={{ color: 'var(--color-text-tertiary)', fontSize: 12, paddingLeft: 16 }}>
            +{playlist.tracks.length - 5} more tracks
          </div>
        )}
      </div>
    </div>
  )
}

function EmptyState({ msg, action, onAction }: { msg: string; action: string; onAction: () => void }) {
  return (
    <div className="empty-state">
      <p>{msg}</p>
      <button className="btn btn-primary" onClick={onAction}>{action}</button>
    </div>
  )
}
