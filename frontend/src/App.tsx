import { useState, useEffect, useCallback, useRef } from 'react'
import { api } from './api'
import type { ConnectionStatus, Playlist, TrackSyncStatus, Mapping, SyncResult, ImportedPlaylist } from './types'
import './app.css'

type PanelLayout = { left: 'spotify' | 'tidal'; right: 'spotify' | 'tidal' }

// Seeded color for playlist avatars — same name always gets same color
const AVATAR_COLORS = ['#5B8DEF','#E8605A','#F5A623','#7ED321','#9B59B6','#1ABC9C','#E67E22','#E91E8C']
const avatarColor = (name: string = '') => AVATAR_COLORS[
  (name || '?').split('').reduce((a, c) => a + c.charCodeAt(0), 0) % AVATAR_COLORS.length
]

const cap = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)
const TIDAL_THROTTLE_MS = 650 // matches TidalThrottler MinGapMs

export default function App() {
  const [status, setStatus]                       = useState<ConnectionStatus | null>(null)
  const [spotifyPlaylists, setSpotifyPlaylists]   = useState<Playlist[]>([])
  const [tidalPlaylists, setTidalPlaylists]       = useState<Playlist[]>([])
  const [mappings, setMappings]                   = useState<Mapping[]>([])
  const [layout, setLayout]                       = useState<PanelLayout>({ left: 'spotify', right: 'tidal' })
  const [selectedLeft, setSelectedLeft]           = useState<Playlist | null>(null)
  const [selectedRight, setSelectedRight]         = useState<Playlist | null>(null)
  const [importedPlaylist, setImportedPlaylist]   = useState<ImportedPlaylist | null>(null)
  const [urlInput, setUrlInput]                   = useState('')
  const [urlLoading, setUrlLoading]               = useState(false)
  const [trackStatus, setTrackStatus]             = useState<TrackSyncStatus[]>([])
  const [syncing, setSyncing]                     = useState(false)
  const [syncProgress, setSyncProgress]           = useState<{ done: number; total: number; eta: number } | null>(null)
  const [syncResult, setSyncResult]               = useState<SyncResult | null>(null)
  const [autoSync, setAutoSync]                   = useState(true)
  const [searchLeft, setSearchLeft]               = useState('')
  const [searchRight, setSearchRight]             = useState('')
  const [activeMapping, setActiveMapping]         = useState<Mapping | null>(null)
  const [toast, setToast]                         = useState<{ msg: string; type: 'success' | 'error' } | null>(null)
  const [confirmDisconnect, setConfirmDisconnect] = useState<string | null>(null)
  const [unmatchedOpen, setUnmatchedOpen]         = useState(false)
  const progressTimer                             = useRef<ReturnType<typeof setInterval> | null>(null)

  const showToast = (msg: string, type: 'success' | 'error' = 'success') => {
    setToast({ msg, type })
    setTimeout(() => setToast(null), 4000)
  }

  const loadAll = useCallback(async () => {
    try {
      const s = await api.auth.status()
      setStatus(s)
      const m = await api.sync.getMappings()
      setMappings(m)
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
    if (params.has('error')) {
      const msg = params.get('error') === 'oauth_state_invalid'
        ? 'Login session expired — please try connecting again'
        : 'Authentication failed — please try again'
      showToast(msg, 'error')
      window.history.replaceState({}, '', '/')
    }
  }, [])

  // Cleanup progress timer on unmount
  useEffect(() => () => { if (progressTimer.current) clearInterval(progressTimer.current) }, [])

  const leftPlaylists  = layout.left  === 'spotify' ? spotifyPlaylists : tidalPlaylists
  const rightPlaylists = layout.right === 'spotify' ? spotifyPlaylists : tidalPlaylists
  const leftConnected  = status?.[layout.left]?.connected  ?? false
  const rightConnected = status?.[layout.right]?.connected ?? false
  const neitherConnected = !status?.spotify.connected && !status?.tidal.connected

  const importTargetService   = importedPlaylist
    ? (importedPlaylist.service === 'spotify' ? 'tidal' : 'spotify') as 'spotify' | 'tidal'
    : null
  const importTargetPlaylist  = importTargetService
    ? (importTargetService === layout.left ? selectedLeft : selectedRight)
    : null
  const importTargetConnected = importTargetService ? (status?.[importTargetService]?.connected ?? false) : false
  // Can sync if: existing mapping, OR source+target both selected, OR import with target connected
  const sourceSelected = selectedLeft !== null
  const targetServiceConnected = status?.[layout.right]?.connected ?? false
  const syncEnabled = (!importedPlaylist && (!!activeMapping || (sourceSelected && targetServiceConnected)))
    || (!!importedPlaylist && importTargetConnected)

  // Simulate progress during Tidal sync (we know throttle rate)
  const startProgressSimulation = (trackCount: number, targetService: string) => {
    if (targetService !== 'tidal') return
    const totalMs = trackCount * TIDAL_THROTTLE_MS
    const start = Date.now()
    setSyncProgress({ done: 0, total: trackCount, eta: Math.ceil(totalMs / 1000) })
    progressTimer.current = setInterval(() => {
      const elapsed = Date.now() - start
      const done = Math.min(Math.floor(elapsed / TIDAL_THROTTLE_MS), trackCount - 1)
      const eta = Math.max(0, Math.ceil((totalMs - elapsed) / 1000))
      setSyncProgress({ done, total: trackCount, eta })
    }, 500)
  }

  const stopProgressSimulation = () => {
    if (progressTimer.current) { clearInterval(progressTimer.current); progressTimer.current = null }
    setSyncProgress(null)
  }

  const selectLeft = async (pl: Playlist) => {
    setSelectedLeft(pl)
    setSyncResult(null)
    setTrackStatus([])
    setUnmatchedOpen(false)
    const existing = mappings.find(m => m.sourcePlaylistId === pl.id || m.targetPlaylistId === pl.id)
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
      setSelectedLeft(null); setSelectedRight(null)
      setActiveMapping(null); setTrackStatus([]); setSyncResult(null)
      setUrlInput('')
      showToast(`Loaded "${result.name}" — ${result.trackCount} tracks`)
    } catch (e: any) { showToast(e.message, 'error') }
    finally { setUrlLoading(false) }
  }

  const handleSync = async () => {
    setSyncing(true)
    setSyncResult(null)
    setUnmatchedOpen(false)
    try {
      if (importedPlaylist && importTargetService) {
        startProgressSimulation(importedPlaylist.trackCount, importTargetService)
        const result = await api.sync.syncFromTracks({
          sourceTracks:       importedPlaylist.tracks,
          targetService:      importTargetService,
          targetPlaylistId:   importTargetPlaylist?.id ?? '',
          targetPlaylistName: importedPlaylist.name,
          direction:          'sourceToTarget',
        })
        stopProgressSimulation()
        setSyncResult(result)
        if (result.success) {
          showToast(`Synced: +${result.tracksAdded} tracks${result.tracksSkipped ? `, ${result.tracksSkipped} skipped` : ''}`)
          await loadAll()   // refresh playlists so newly created one appears
        } else {
          showToast(result.error ?? 'Sync failed', 'error')
        }
        return
      }
      if (!activeMapping && selectedLeft) {
        // No mapping yet — fetch source tracks and sync into target (creates new playlist if no target selected)
        const targetSvc = layout.right
        startProgressSimulation(selectedLeft.trackCount, targetSvc)
        const result = await api.sync.syncFromTracks({
          sourceTracks:       [],   // empty = backend will fetch from service using userId + playlistId
          targetService:      targetSvc,
          targetPlaylistId:   selectedRight?.id ?? '',
          targetPlaylistName: selectedLeft.name,
          direction:          'sourceToTarget',
          sourceService:      layout.left,
          sourcePlaylistId:   selectedLeft.id,
        })
        stopProgressSimulation()
        setSyncResult(result)
        result.success
          ? showToast(`Synced: +${result.tracksAdded} tracks${result.tracksSkipped ? `, ${result.tracksSkipped} skipped` : ''}`)
          : showToast(result.error ?? 'Sync failed', 'error')
        await loadAll()
        return
      }
      if (!activeMapping) return
      const trackCount = (selectedLeft?.trackCount ?? 0)
      const targetSvc  = layout.left === 'spotify' ? 'tidal' : 'spotify'
      startProgressSimulation(trackCount, targetSvc)
      const result = await api.sync.triggerSync(activeMapping.id)
      stopProgressSimulation()
      setSyncResult(result)
      if (result.success) {
        showToast(`Synced: +${result.tracksAdded} tracks${result.tracksSkipped ? `, ${result.tracksSkipped} skipped` : ''}`)
        const s = await api.sync.getStatus(activeMapping.id)
        setTrackStatus(s)
        await loadAll()
      } else showToast(result.error ?? 'Sync failed', 'error')
    } catch (e: any) {
      stopProgressSimulation()
      showToast(e.message, 'error')
    }
    finally { setSyncing(false) }
  }

  const handleDeleteMapping = async () => {
    if (!activeMapping) return
    await api.sync.deleteMapping(activeMapping.id)
    setActiveMapping(null); setTrackStatus([]); await loadAll()
    showToast('Mapping removed')
  }

  const handleDisconnect = async (service: string) => {
    await api.auth.disconnect(service)
    setConfirmDisconnect(null)
    await loadAll()
    showToast(`${cap(service)} disconnected`)
  }

  const filteredLeft  = leftPlaylists.filter(p => p.name.toLowerCase().includes(searchLeft.toLowerCase()))
  const filteredRight = rightPlaylists.filter(p => p.name.toLowerCase().includes(searchRight.toLowerCase()))
  const synced    = trackStatus.filter(t => t.status === 'synced').length
  const missing   = trackStatus.filter(t => t.status === 'missing').length
  const newTracks = trackStatus.filter(t => t.status === 'new').length

  // ── Onboarding screen ──────────────────────────────────────────────────────
  if (status && neitherConnected) {
    return (
      <div className="app">
        {toast && <div className={`toast toast-${toast.type}`}>{toast.msg}</div>}
        <div className="onboarding">
          <div className="onboarding-logo">
            <svg width="32" height="32" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
              <path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/>
            </svg>
            <span>playlistsync</span>
          </div>
          <p className="onboarding-sub">Sync playlists between Spotify and Tidal. Connect at least one service to get started.</p>
          <div className="onboarding-services">
            <button className="service-connect-btn spotify" onClick={api.auth.spotifyLogin}>
              <span className="dot dot-spotify"/>
              Connect Spotify
            </button>
            <button className="service-connect-btn tidal" onClick={api.auth.tidalLogin}>
              <span className="dot dot-tidal"/>
              Connect Tidal
            </button>
          </div>
          <p className="onboarding-hint">Or paste a public playlist URL below to sync without logging in</p>
          <div className="onboarding-url">
            <input className="url-input" placeholder="https://open.spotify.com/playlist/… or tidal.com/browse/playlist/…"
              value={urlInput} onChange={e => setUrlInput(e.target.value)}
              onKeyDown={e => e.key === 'Enter' && handleImportUrl()} />
            <button className="btn btn-primary" onClick={handleImportUrl} disabled={urlLoading || !urlInput.trim()}>
              {urlLoading ? 'Loading…' : 'Import'}
            </button>
          </div>
        </div>
      </div>
    )
  }

  // ── Main app ───────────────────────────────────────────────────────────────
  return (
    <div className="app">
      {toast && <div className={`toast toast-${toast.type}`}>{toast.msg}</div>}

      {confirmDisconnect && (
        <div className="confirm-overlay" onClick={() => setConfirmDisconnect(null)}>
          <div className="confirm-dialog" onClick={e => e.stopPropagation()}>
            <p>Disconnect {cap(confirmDisconnect)}?</p>
            <p className="confirm-sub">Your sync mappings will be preserved.</p>
            <div className="confirm-actions">
              <button className="btn" onClick={() => setConfirmDisconnect(null)}>Cancel</button>
              <button className="btn btn-danger" onClick={() => handleDisconnect(confirmDisconnect)}>Disconnect</button>
            </div>
          </div>
        </div>
      )}

      <header className="topbar">
        <span className="logo">
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M9 18V5l12-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="18" cy="16" r="3"/>
          </svg>
          playlistsync
        </span>
        <div className="auth-pills">
          {status?.spotify.connected
            ? <div className="pill pill-connected" onClick={() => setConfirmDisconnect('spotify')} title="Click to disconnect">
                <span className="dot dot-spotify"/>{status.spotify.displayName ?? 'Spotify'}
              </div>
            : <div className="pill pill-disconnected" onClick={api.auth.spotifyLogin}>
                <span className="dot dot-off"/>Connect Spotify
              </div>}
          {status?.tidal.connected
            ? <div className="pill pill-connected" onClick={() => setConfirmDisconnect('tidal')} title="Click to disconnect">
                <span className="dot dot-tidal"/>{status.tidal.displayName ?? 'Tidal'}
              </div>
            : <div className="pill pill-disconnected" onClick={api.auth.tidalLogin}>
                <span className="dot dot-off"/>Connect Tidal
              </div>}
        </div>
      </header>

      <div className="url-bar">
        <input className="url-input"
          placeholder="Paste a public Spotify or Tidal playlist URL to import without logging in…"
          value={urlInput} onChange={e => setUrlInput(e.target.value)}
          onKeyDown={e => e.key === 'Enter' && handleImportUrl()} />
        <button className="btn btn-primary" onClick={handleImportUrl} disabled={urlLoading || !urlInput.trim()}>
          {urlLoading ? 'Loading…' : 'Import'}
        </button>
      </div>

      {importedPlaylist && (
        <div className={`import-bar ${importTargetConnected ? '' : 'import-bar-warn'}`}>
          <span className="import-bar-icon">↓</span>
          <span>
            Imported <strong>{importedPlaylist.name}</strong> from {cap(importedPlaylist.service)}
            {' · '}read-only — can only sync <em>into</em> {cap(importTargetService ?? '')}
            {importTargetConnected
              ? importTargetPlaylist ? ` · into "${importTargetPlaylist.name}"` : ' · pick a target or sync now to create one'
              : ` — connect ${cap(importTargetService ?? '')} first`}
          </span>
          <button className="unlink-btn" onClick={() => setImportedPlaylist(null)}>✕</button>
        </div>
      )}

      <div className="main">
        <Panel
          side="left" service={layout.left} connected={leftConnected}
          playlists={filteredLeft} selected={selectedLeft}
          importedPlaylist={importedPlaylist?.service === layout.left ? importedPlaylist : null}
          search={searchLeft} onSearch={setSearchLeft}
          onSelect={selectLeft}
          onClearImport={() => setImportedPlaylist(null)}
          onConnect={() => layout.left === 'spotify' ? api.auth.spotifyLogin() : api.auth.tidalLogin()}
          label={importedPlaylist?.service === layout.left ? 'Imported' : 'Source'}
          trackStatus={trackStatus} synced={synced} missing={missing} newTracks={newTracks}
          activeMapping={activeMapping} selectedOther={selectedRight}
          onDeleteMapping={handleDeleteMapping} showImportOnRight={importedPlaylist?.service === layout.right}
        />

        <div className="middle-col">
          <button className="dir-btn" onClick={() => setLayout(l => ({ left: l.right, right: l.left }))} title="Swap panels">⇄</button>
          <button className={`sync-orb ${syncing ? 'syncing' : ''} ${!syncEnabled ? 'disabled' : ''}`}
            onClick={handleSync} disabled={!syncEnabled || syncing} title="Sync now">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8"/>
              <path d="M21 3v5h-5"/>
              <path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16"/>
              <path d="M8 16H3v5"/>
            </svg>
          </button>
          <button className={`auto-btn ${autoSync ? 'auto-on' : ''}`}
            onClick={() => setAutoSync(!autoSync)} title={autoSync ? 'Auto-sync on' : 'Auto-sync off'}>
            <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/>
            </svg>
          </button>
        </div>

        <Panel
          side="right" service={layout.right} connected={rightConnected}
          playlists={filteredRight} selected={selectedRight}
          importedPlaylist={importedPlaylist?.service === layout.right ? importedPlaylist : null}
          search={searchRight} onSearch={setSearchRight}
          onSelect={p => setSelectedRight(p)}
          onClearImport={() => setImportedPlaylist(null)}
          onConnect={() => layout.right === 'spotify' ? api.auth.spotifyLogin() : api.auth.tidalLogin()}
          label={importedPlaylist?.service === layout.right ? 'Imported' : 'Target'}
          trackStatus={[]} synced={0} missing={0} newTracks={0}
          activeMapping={null} selectedOther={selectedLeft}
          onDeleteMapping={() => {}}
          showImportOnRight={false}
          importMsg={importedPlaylist && !rightConnected && importTargetService === layout.right
            ? `Connect ${cap(layout.right)} to sync the imported playlist into it` : undefined}
        />
      </div>

      {syncing && syncProgress && (
        <div className="sync-progress">
          <div className="sync-progress-bar" style={{ width: `${(syncProgress.done / syncProgress.total) * 100}%` }}/>
          <span className="sync-progress-text">
            Syncing {syncProgress.done}/{syncProgress.total} tracks
            {syncProgress.eta > 0 ? ` · ~${syncProgress.eta}s remaining (Tidal rate limit)` : ' · almost done…'}
          </span>
        </div>
      )}

      <footer className="bottombar">
        <span className="sync-meta">
          {importedPlaylist
            ? importTargetConnected
              ? importTargetPlaylist
                ? `Will sync into "${importTargetPlaylist.name}"`
                : `No target selected — a new playlist will be created`
              : `Connect ${cap(importTargetService ?? '')} to sync`
            : activeMapping?.lastSyncedAt
              ? `Last synced ${new Date(activeMapping.lastSyncedAt).toLocaleString()}${activeMapping.autoSync ? ' · auto hourly' : ''}`
              : activeMapping ? 'Never synced' : 'Select a playlist to sync'}
        </span>
        {syncResult && syncResult.unmatchedCount > 0 && (
          <button className="unmatched-toggle" onClick={() => setUnmatchedOpen(o => !o)}>
            {syncResult.unmatchedCount} unmatched {unmatchedOpen ? '▴' : '▾'}
          </button>
        )}
        <button className="btn btn-primary" onClick={handleSync} disabled={!syncEnabled || syncing}>
          {syncing ? syncProgress ? `Syncing ${syncProgress.done}/${syncProgress.total}…` : 'Syncing…' : 'Sync now'}
        </button>
      </footer>

      {syncResult && syncResult.unmatched.length > 0 && unmatchedOpen && (
        <div className="unmatched-list open">
          <div className="unmatched-header">{syncResult.unmatched.length} tracks couldn't be matched</div>
          {syncResult.unmatched.map((t, i) => (
            <div key={i} className="unmatched-item">
              <div className="unmatched-track-info">
                <span className="unmatched-name">{t.name}</span>
                <span className="unmatched-artist">{t.artist}</span>
              </div>
              <span className="unmatched-reason">{t.reason}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

// ── Panel component ───────────────────────────────────────────────────────────
function Panel({ side, service, connected, playlists, selected, importedPlaylist,
  search, onSearch, onSelect, onClearImport, onConnect, label,
  trackStatus, synced, missing, newTracks, activeMapping, selectedOther,
  onDeleteMapping, showImportOnRight, importMsg }: {
  side: 'left' | 'right'; service: string; connected: boolean
  playlists: Playlist[]; selected: Playlist | null
  importedPlaylist: ImportedPlaylist | null
  search: string; onSearch: (s: string) => void
  onSelect: (p: Playlist) => void; onClearImport: () => void; onConnect: () => void
  label: string
  trackStatus: TrackSyncStatus[]; synced: number; missing: number; newTracks: number
  activeMapping: Mapping | null; selectedOther: Playlist | null
  onDeleteMapping: () => void; showImportOnRight: boolean
  importMsg?: string
}) {
  const cap = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)
  return (
    <div className="panel">
      <div className="panel-header">
        <div className="panel-meta">
          <span className="panel-label">{label}</span>
          <div className="service-name-row">
            <span className={`dot dot-${service}`}/>
            <span>{cap(service)}</span>
            {!connected && <span className="not-logged-in-badge">not logged in</span>}
          </div>
        </div>
        {connected && !importedPlaylist && (
          <input className="search-input" placeholder="Search playlists…"
            value={search} onChange={e => onSearch(e.target.value)} />
        )}
        {side === 'right' && activeMapping && !importedPlaylist && (
          <div className="mapping-bar">
            <span className="map-chip map-chip-l">{selectedOther?.name}</span>
            <span className="map-arrow">↔</span>
            <span className="map-chip map-chip-r">{selected?.name}</span>
            <button className="unlink-btn" onClick={onDeleteMapping}>✕</button>
          </div>
        )}
        {side === 'right' && !activeMapping && !importedPlaylist && (
          <div className="mapping-bar">
            <span className="hint-text">{selectedOther ? '← Pick a target' : '← Select a source first'}</span>
          </div>
        )}
      </div>

      {/* Track status view (left panel, after selecting a mapped playlist) */}
      {side === 'left' && trackStatus.length > 0 && !importedPlaylist ? (
        <div className="track-list">
          <div className="track-stats">
            <span className="stat stat-synced">{synced} synced</span>
            {missing   > 0 && <span className="stat stat-missing">{missing} not found</span>}
            {newTracks > 0 && <span className="stat stat-new">{newTracks} new</span>}
          </div>
          {trackStatus.map((ts, i) => (
            <div key={ts.track.id} className="track-item">
              <span className="track-num">{i + 1}</span>
              <div className="track-art" style={{ background: avatarColor(ts.track.name) }}>
                <span style={{ fontSize: 10, color: '#fff', fontWeight: 600 }}>{ts.track.name[0]}</span>
              </div>
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
      ) : importedPlaylist ? (
        <ImportedPlaylistCard playlist={importedPlaylist} onClear={onClearImport} />
      ) : (
        <div className="playlist-list">
          {!connected ? (
            <EmptyState
              msg={importMsg ?? `Connect ${cap(service)} to see your playlists`}
              action={`Connect ${cap(service)}`}
              onAction={onConnect} />
          ) : playlists.length === 0 && search ? (
            <div className="empty-state"><p>No playlists match "{search}"</p></div>
          ) : playlists.map(p => (
            <PlaylistRow key={p.id} playlist={p}
              selected={selected?.id === p.id}
              onSelect={() => onSelect(p)}
              accent={service} />
          ))}
        </div>
      )}
    </div>
  )
}

// ── Sub-components ────────────────────────────────────────────────────────────
function PlaylistRow({ playlist, selected, onSelect, accent }:
  { playlist: Playlist; selected: boolean; onSelect: () => void; accent: string }) {
  const color = avatarColor(playlist.name)
  return (
    <div className={`playlist-row ${selected ? `selected selected-${accent}` : ''}`} onClick={onSelect}>
      <div className="pl-art" style={{ background: color }}>
        <span style={{ color: '#fff', fontWeight: 600, fontSize: 14 }}>{(playlist.name?.[0] ?? '?').toUpperCase()}</span>
      </div>
      <div className="pl-info">
        <div className="pl-name">{playlist.name}</div>
        <div className="pl-meta">
          {playlist.trackCount} tracks
          {playlist.isMapped && <span className="mapped-badge">synced</span>}
        </div>
      </div>
    </div>
  )
}

function ImportedPlaylistCard({ playlist, onClear }:
  { playlist: ImportedPlaylist; onClear: () => void }) {
  const color = avatarColor(playlist.name)
  return (
    <div className="imported-card">
      <div className="imported-header">
        <div className="pl-art" style={{ background: color }}>
          <span style={{ color: '#fff', fontWeight: 600, fontSize: 14 }}>{(playlist.name?.[0] ?? '?').toUpperCase()}</span>
        </div>
        <div className="pl-info">
          <div className="pl-name">{playlist.name}</div>
          <div className="pl-meta">{playlist.trackCount} tracks · imported · read-only</div>
        </div>
        <button className="unlink-btn" onClick={onClear}>✕</button>
      </div>
      <div className="imported-tracks">
        {playlist.tracks.slice(0, 6).map((t, i) => (
          <div key={t.id} className="track-item">
            <span className="track-num">{i + 1}</span>
            <div className="track-art" style={{ background: avatarColor(t.name) }}>
              <span style={{ fontSize: 9, color: '#fff', fontWeight: 600 }}>{t.name?.[0] ?? '?'}</span>
            </div>
            <div className="track-info">
              <div className="track-name">{t.name}</div>
              <div className="track-artist">{t.artist}</div>
            </div>
          </div>
        ))}
        {playlist.tracks.length > 6 && (
          <div style={{ padding: '8px 16px', fontSize: 12, color: 'var(--text-3)' }}>
            +{playlist.tracks.length - 6} more tracks
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
