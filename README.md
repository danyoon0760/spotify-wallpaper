# Spotify Wallpaper

A live desktop wallpaper that reacts to the song playing in Spotify (Windows 10/11).
It shows the album art in its original colors over a dark blurred background, and can show synced lyrics.

## Running it

```
dist\SpotifyWallpaper.exe            # attach to the desktop (behind the icons)
dist\SpotifyWallpaper.exe --window   # run as a normal window (for debugging)
```

Control it from the **tray icon** (bottom right of the taskbar):

| Item | Description |
|---|---|
| Show lyrics (`Ctrl+Alt+L`, double-click the tray icon) | On: album art left, lyrics right (a thin waveform if the song has no lyrics) / Off: album art centered |
| Album display monitor | With 2+ monitors, pick which one shows the album and lyrics (the rest show only the background) |
| High-res album art | Searches iTunes (1200px) and Deezer (1000px) at the same time (the Spotify thumbnail is 300px) |
| Frame limit | 30 / 60 FPS |
| Start with Windows | Registers it in HKCU Run |
| Reload / Developer tools / Open log folder | For development |

Settings: `%APPDATA%\SpotifyWallpaper\settings.json`, log: `%APPDATA%\SpotifyWallpaper\log.txt`

## Structure

```
host/   .NET 10 WinForms + WebView2 host
  WallpaperApp.cs            wiring: media → album art, lyrics → every monitor's window
  DisplayManager.cs          one window per monitor (selected = album/lyrics, others = background), occlusion pause, rebuilds
  WallpaperForm.cs           WebView2 window
  DesktopHost.cs             attaches windows behind the desktop icons (supports the 24H2+ raised-desktop layout)
  TrayMenu.cs                tray icon and menu
  MediaWatcher.cs            Windows media controls (SMTC): track, album art, playback state, playback clock
  AudioAnalyzer.cs           audio → 64 log-spaced bands (Spotify's own audio first, system audio as fallback)
  ProcessLoopbackCapture.cs  captures a single process's audio (Windows process loopback)
  LyricsService.cs           synced lyrics from LRCLIB + NetEase (only if duration matches within ±3s), disk cache
  ArtworkService.cs          high-res album art (iTunes song/album + Deezer, searched in parallel)
  Http.cs, Settings.cs, Native.cs, Log.cs, AutoStart.cs
web/    renderer (HTML + WebGL2)
  src/main.js        entry point: wires the modules together, main loop
  src/player.js      host messages → playback state and position clock
  src/layout.js      album/lyrics placement
  src/renderer.js    WebGL texture and uniform management
  src/shader.js      blurred background, album art (rounded corners), ink-bleed transition
  src/lyrics.js      synced lyrics, highlighted line by line
  src/wave.js        thin waveform shown in place of lyrics when a song has none
  src/audio.js       auto gain, bass/mid/high envelopes
  src/palette.js     accent color extraction from the album art
  src/mock.js        mock data for browser development
dev/    dev server, CDP debugging script (CDP_PAGE=bg selects the background page)
```

## Development

Work on the renderer in a browser with mock data:

```
node dev/serve.mjs     # http://localhost:5173
```

Keys: `L` lyrics, `K` simulate a song without lyrics, `Space` play/pause, `N` next track, `I` idle, `→` +10 seconds.
Drop an image file to use it as the album art, or drop a music file to analyze its real spectrum.

The host loads the source `web/` folder directly, so edits to the renderer show up after **tray → Reload**.
After changing host code, rebuild:

```
dotnet publish host -c Release -o dist
```

Running with the `WALLPAPER_DEBUG_PORT=9229` environment variable opens a remote debugging port, so you can use
`node dev/cdp-eval.mjs "<JS expression>"` and `node dev/cdp-eval.mjs --shot out.png`.

## Notes

- It follows Spotify only. Other apps' media (YouTube in a browser, etc.) and their sound are ignored; when Spotify isn't running, only the background is shown.
- Lyrics search sends the song title and artist name to LRCLIB and NetEase; high-res album art search sends them to iTunes and Deezer. You can turn off high-res album art from the tray.
- With multiple monitors, only the monitor chosen in the tray shows the album and lyrics; the others show the same blurred background only. Windows are rebuilt automatically when a monitor is connected or disconnected, or the resolution changes.
- Rendering pauses automatically, per monitor, when a maximized or fullscreen window covers that monitor.
- Frame rates are set per layer: the full-screen WebGL layer is redrawn at the configured FPS only during transitions or layout moves, and otherwise at 24 FPS while playing (10 FPS when paused or on background-only monitors). Audio analysis runs at 30Hz and is sent only to the album monitor.
