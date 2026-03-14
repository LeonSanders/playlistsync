import { useState, useEffect, useCallback } from 'react'
import { api } from './api'
import type { ConnectionStatus, Playlist, TrackSyncStatus, Mapping, SyncResult } from './types'
import './app.css'

type SyncDir = 'bidirectional' | 'sourceToTarget' | 'targetToSource'

export default function App() {
  const [status, setStatus] = useState<ConnectionStatus | null>(null)
  const [spotifyPlaylists, setSpotifyPlaylists] = useState<Playlist[]>([])
  const [tidalPlaylists, setTidalPlaylists] = useState<Playlist[]>([])
  const [mappings, setMappings] = useState<Mapping[]>([])
  const [selectedSource, setSelectedSource] = useState<Playlist | null>(null)
  const [selectedTarget, setSelectedTarget] = useState<Playlist | null>(null)
  const [trackStatus, setTrackStatus] = useState<TrackSyncStatus[]>([])
  const [syncing, setSyncing] = useState(false)
  const [syncResult, setSyncResult] = useState<SyncResult | null>(null)
  const [syncDir, setSyncDir] = useState<SyncDir>('bidirectional')
  const [autoSync, setAutoSync] = useState(true)
  const [searchSrc, setSearchSrc] = useState('')
  const [searchTgt, setSearchTgt] = useState('')
  const [activeMapping, setActiveMapping] = useState<Mapping | null>(null)
  const [toast, setToast] = useState<{ msg: string; type: 'success' | 'error' } | null>(null)

  const showToast = (msg: string, type: 'success' | 'error' = 'success') => {
    setToast({ msg, type })
    setTimeout(() => setToast(null), 3500)
  }

  const loadAll = useCallback(async () => {
    try {
      const s = await api.auth.status()
      setStatus(s)
      const [m] = await Promise.all([api.sync.getMappings()])
      setMappings(m)
      if (s.spotify.connected) setSpotifyPlaylists(await api.playlists.getSpotify())
      if (s.tidal.connected) setTidalPlaylists(await api.playlists.getTidal())
    } catch (e) {
      console.error(e)
    }
  }, [])

  useEffect(() => { loadAll() }, [loadAll])

  useEffect(() => {
    const params = new URLSearchParams(window.location.search)
    if (params.has('spotify')) { showToast('Spotify connected!'); window.history.replaceState({}, '', '/') }
    if (params.has('tidal')) { showToast('Tidal connected!'); window.history.replaceState({}, '', '/') }
  }, [])

  const selectSource = async (pl: Playlist) => {
    setSelectedSource(pl)
    setSyncResult(null)
    setTrackStatus([])
    const existing = mappings.find(m => m.sourcePlaylistId === pl.id || m.targetPlaylistId === pl.id)
    setActiveMapping(existing ?? null)
    if (existing) {
      const status = await api.sync.getStatus(existing.id)
      setTrackStatus(status)
      const tgt = tidalPlaylists.find(t => t.id === existing.targetPlaylistId)
        ?? spotifyPlaylists.find(t => t.id === existing.targetPlaylistId)
      setSelectedTarget(tgt ?? null)
    }
  }

  const handleCreateMapping = async () => {
    if (!selectedSource || !selectedTarget) return
    const id = await api.sync.createMapping({
      sourceService: 'spotify',
      sourcePlaylistId: selectedSource.id,
      targetService: 'tidal',
      targetPlaylistId: selectedTarget.id,
      direction: syncDir,
      autoSync,
    })
    await loadAll()
    showToast('Mapping created')
    const newMapping = await api.sync.getMappings().then(m => m.find(x => x.id === id))
    setActiveMapping(newMapping ?? null)
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
        const status = await api.sync.getStatus(activeMapping.id)
        setTrackStatus(status)
        await loadAll()
      } else {
        showToast(result.error ?? 'Sync failed', 'error')
      }
    } catch (e: any) {
      showToast(e.message, 'error')
    } finally {
      setSyncing(false)
    }
  }

  const handleDeleteMapping = async () => {
    if (!activeMapping) return
    await api.sync.deleteMapping(activeMapping.id)
    setActiveMapping(null)
    setTrackStatus([])
    await loadAll()
    showToast('Mapping removed')
  }

  const filteredSource = spotifyPlaylists.filter(p => p.name.toLowerCase().includes(searchSrc.toLowerCase()))
  const filteredTarget = tidalPlaylists.filter(p => p.name.toLowerCase().includes(searchTgt.toLowerCase()))

  const synced = trackStatus.filter(t => t.status === 'synced').length
  const missing = trackStatus.filter(t => t.status === 'missing').length
  const newTracks = trackStatus.filter(t => t.status === 'new').length

  return (
    <div className="app">
      {toast && <div className={`toast toast-${toast.type}`}>{toast.msg}</div>}

      <header className="topbar">
        <div className="topbar-left">
          <span className="logo">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/>
            </svg>
            playlistsync
          </span>
        </div>
        <div className="auth-pills">
          {status ? (
            <>
              <ServicePill service="spotify" connected={status.spotify.connected} name={status.spotify.displayName} onConnect={api.auth.spotifyLogin} onDisconnect={() => api.auth.disconnect('spotify').then(loadAll)} />
              <ServicePill service="tidal" connected={status.tidal.connected} name={status.tidal.displayName} onConnect={api.auth.tidalLogin} onDisconnect={() => api.auth.disconnect('tidal').then(loadAll)} />
            </>
          ) : <span className="loading-text">Connecting…</span>}
        </div>
      </header>

      <div className="main">
        <div className="panel panel-source">
          <div className="panel-header">
            <div className="panel-meta">
              <span className="panel-label">Source</span>
              <span className="service-name"><span className="dot dot-spotify" />Spotify</span>
            </div>
            <input className="search-input" placeholder="Search playlists…" value={searchSrc} onChange={e => setSearchSrc(e.target.value)} />
          </div>
          <div className="playlist-list">
            {!status?.spotify.connected ? (
              <EmptyState msg="Connect Spotify to see your playlists" action="Connect Spotify" onAction={api.auth.spotifyLogin} />
            ) : filteredSource.map(p => (
              <PlaylistRow key={p.id} playlist={p} selected={selectedSource?.id === p.id} onSelect={() => selectSource(p)} accent="spotify" />
            ))}
          </div>
        </div>

        <div className="middle-col">
          <DirButton dir={syncDir} onChange={setSyncDir} />
          <button className={`sync-orb ${syncing ? 'syncing' : ''} ${!activeMapping ? 'disabled' : ''}`} onClick={handleSync} title="Sync now" disabled={!activeMapping || syncing}>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8"/><path d="M21 3v5h-5"/><path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16"/><path d="M8 16H3v5"/>
            </svg>
          </button>
          <AutoSyncToggle value={autoSync} onChange={setAutoSync} />
        </div>

        <div className="panel panel-target">
          <div className="panel-header">
            <div className="panel-meta">
              <span className="panel-label">Target</span>
              <span className="service-name"><span className="dot dot-tidal" />Tidal</span>
            </div>
            {activeMapping ? (
              <div className="mapping-bar">
                <span className="map-chip map-chip-sp">{selectedSource?.name}</span>
                <span className="map-arrow">↔</span>
                <span className="map-chip map-chip-ti">{activeMapping.targetPlaylistName || selectedTarget?.name}</span>
                <button className="unlink-btn" onClick={handleDeleteMapping} title="Remove mapping">✕</button>
              </div>
            ) : selectedSource ? (
              <div className="mapping-bar">
                <span className="hint-text">Pick a Tidal playlist to map →</span>
              </div>
            ) : (
              <div className="mapping-bar">
                <span className="hint-text">← Select a source playlist</span>
              </div>
            )}
          </div>

          {trackStatus.length > 0 ? (
            <div className="track-list">
              <div className="track-stats">
                <span className="stat stat-synced">{synced} synced</span>
                {missing > 0 && <span className="stat stat-missing">{missing} not found</span>}
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
          ) : (
            <div className="playlist-list">
              {!status?.tidal.connected ? (
                <EmptyState msg="Connect Tidal to see your playlists" action="Connect Tidal" onAction={api.auth.tidalLogin} />
              ) : filteredTarget.map(p => (
                <PlaylistRow key={p.id} playlist={p} selected={selectedTarget?.id === p.id}
                  onSelect={async () => {
                    setSelectedTarget(p)
                    if (selectedSource && !activeMapping) await handleCreateMapping()
                  }} accent="tidal" />
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

function ServicePill({ service, connected, name, onConnect, onDisconnect }:
  { service: string; connected: boolean; name?: string; onConnect: () => void; onDisconnect: () => void }) {
  return connected ? (
    <div className="pill pill-connected" onClick={onDisconnect} title="Click to disconnect">
      <span className={`dot dot-${service}`} />
      {name ?? service}
    </div>
  ) : (
    <div className="pill pill-disconnected" onClick={onConnect}>
      <span className="dot dot-off" />
      Connect {service}
    </div>
  )
}

function PlaylistRow({ playlist, selected, onSelect, accent }:
  { playlist: Playlist; selected: boolean; onSelect: () => void; accent: 'spotify' | 'tidal' }) {
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

function EmptyState({ msg, action, onAction }: { msg: string; action: string; onAction: () => void }) {
  return (
    <div className="empty-state">
      <p>{msg}</p>
      <button className="btn btn-primary" onClick={onAction}>{action}</button>
    </div>
  )
}

function DirButton({ dir, onChange }: { dir: SyncDir; onChange: (d: SyncDir) => void }) {
  const cycle: SyncDir[] = ['bidirectional', 'sourceToTarget', 'targetToSource']
  const next = () => onChange(cycle[(cycle.indexOf(dir) + 1) % cycle.length])
  const labels = { bidirectional: '⇄', sourceToTarget: '→', targetToSource: '←' }
  return (
    <button className="dir-btn" onClick={next} title={`Direction: ${dir}`}>
      {labels[dir]}
    </button>
  )
}

function AutoSyncToggle({ value, onChange }: { value: boolean; onChange: (v: boolean) => void }) {
  return (
    <button className={`auto-btn ${value ? 'auto-on' : ''}`} onClick={() => onChange(!value)} title="Toggle auto-sync">
      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/>
      </svg>
    </button>
  )
}
