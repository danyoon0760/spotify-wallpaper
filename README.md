# Spotify Wallpaper

A live desktop wallpaper that reacts to the song playing in Spotify (Windows 10/11).
It shows the album art in its original colors over a dark blurred background, and can show synced lyrics.
When nothing is playing it gets out of the way and your normal wallpaper is shown.

## Running it

```
dist\SpotifyWallpaper.exe            # attach to the desktop (behind the icons)
dist\SpotifyWallpaper.exe --window   # run as a normal window (for debugging)
dist\SpotifyWallpaper.exe --exit     # ask the running instance to quit cleanly
```

Control it from the **tray icon** (bottom right of the taskbar):

| Item | Description |
|---|---|
| Show lyrics (hotkey, double-click the tray icon) | On: album art left, lyrics right (a thin waveform if the song has no lyrics) / Off: album art centered |
| Lyrics sync | Shift the lyrics earlier or later in 0.1 s steps (e.g. for Bluetooth delay). The menu stays open so you can click repeatedly |
| Album display monitor | With 2+ monitors, pick which one shows the album and lyrics (the rest show only the background) |
| High-res album art | Looks up 1200px (iTunes) / 1000px (Deezer) art; the Spotify thumbnail is 300px |
| Frame limit | 30 / 60 FPS |
| Show the original wallpaper when not playing | Hides the windows 30 s after Spotify stops/closes, or after 10 min paused; they come back when playback starts |
| Hotkey… | Press a new combination for "Show lyrics", or turn the hotkey off (default `Ctrl+Alt+L`) |
| Start with Windows | Registers it in HKCU Run |
| Reload / Developer tools / Open log folder | For development |

Files it keeps:

| Path | Contents |
|---|---|
| `%APPDATA%\SpotifyWallpaper\settings.json` | Settings |
| `%APPDATA%\SpotifyWallpaper\log.txt` (+ `log.old.txt`) | Log, rotated at 512 KB |
| `%LOCALAPPDATA%\SpotifyWallpaper\art-cache` | Album art (up to 400 hi-res images, oldest removed first) |
| `%LOCALAPPDATA%\SpotifyWallpaper\lyrics-cache` | Lyrics search results |
| `%LOCALAPPDATA%\SpotifyWallpaper\WebView2` | Browser profile |

## Structure

```
host/   .NET 10 WinForms + WebView2 host
  WallpaperApp.cs            wiring: media → album art, lyrics → every monitor's window; idle hide, lock/sleep, hotkey
  DisplayManager.cs          one window per monitor, occlusion pause, suspend (lock / screen off), sleep/wake, rebuilds
  WallpaperForm.cs           WebView2 window; serves album art to the page from the disk cache
  DesktopHost.cs             attaches windows behind the desktop icons (supports the 24H2+ raised-desktop layout)
  TrayMenu.cs, HotkeyDialog.cs, Hotkey.cs   tray icon, menu, hotkey picker
  MediaWatcher.cs            Windows media controls (SMTC): track, album art, playback state, playback clock
  AudioAnalyzer.cs           audio → 64 log-spaced bands, only at the rate the page asks for
  ProcessLoopbackCapture.cs  captures a single process's audio (Windows process loopback)
  LyricsService.cs           LRCLIB + NetEase requests (rate-limit aware), disk cache
  LyricsMatcher.cs           which candidate to accept (duration within ±3s, speed-variant scaling), LRC parsing — pure functions
  ArtworkService.cs          high-res album art search (iTunes US → JP, Deezer)
  ArtStore.cs                album art disk cache
  Http.cs, Settings.cs, AppPaths.cs, Native.cs, Log.cs, AutoStart.cs
web/    renderer (HTML + WebGL2)
  src/main.js        entry point: wires the modules together, main loop, tells the host how much audio it needs
  src/player.js      host messages → playback state and position clock
  src/bridge.js      host connection; the message protocol is documented here
  src/layout.js      album/lyrics placement
  src/renderer.js    WebGL texture and uniform management
  src/shader.js      blurred background, album art (rounded corners), ink-bleed transition
  src/lyrics.js      synced lyrics, highlighted line by line
  src/wave.js        thin waveform shown in place of lyrics when a song has none
  src/audio.js       auto gain, bass/mid/high envelopes
  src/palette.js     accent color extraction from the album art
  src/mock.js        mock data for browser development
  fonts/             bundled fonts (Pretendard, Noto Sans JP, JetBrains Mono — SIL OFL, see fonts/licenses)
tests/  unit tests for the pure logic (lyrics matching, title comparison, hotkey parsing, art cache)
dev/    dev server, CDP debugging script, font download and icon scripts
build.ps1   test + publish
```

## Development

Work on the renderer in a browser with mock data:

```
node dev/serve.mjs     # http://localhost:5173
```

Keys: `L` lyrics, `K` simulate a song without lyrics, `[` `]` lyrics sync, `Space` play/pause, `N` next track, `I` idle, `→` +10 seconds.
Drop an image file to use it as the album art, or drop a music file to analyze its real spectrum.

The host loads the source `web/` folder directly, so edits to the renderer show up after **tray → Reload**.

Run the tests:

```
dotnet test
```

After changing host code, rebuild. The script runs the tests, asks a running copy to exit, publishes, and starts it again:

```
powershell -ExecutionPolicy Bypass -File build.ps1               # dist\  (needs the .NET 10 Desktop Runtime)
powershell -ExecutionPolicy Bypass -File build.ps1 -Standalone   # dist-standalone\  (no .NET install needed, much bigger)
```

Running with the `WALLPAPER_DEBUG_PORT=9229` environment variable opens a remote debugging port, so you can use
`node dev/cdp-eval.mjs "<JS expression>"` and `node dev/cdp-eval.mjs --shot out.png` (`CDP_PAGE=bg` selects a background-only page).

Other scripts: `node dev/fetch-fonts.mjs` re-downloads the bundled fonts (only needed to change versions),
`dev\make-icon.ps1` redraws `host\app.ico`.

## Notes

- It follows Spotify only. Other apps' media (YouTube in a browser, etc.) and their sound are ignored.
- Network use: lyrics search sends the song title and artist name to LRCLIB and NetEase; high-res album art search sends
  them to iTunes and Deezer (you can turn that off from the tray). Nothing else is contacted — fonts are bundled.
  Each song is looked up once; results are cached on disk.
- With multiple monitors, only the monitor chosen in the tray shows the album and lyrics; the others show the same blurred
  background only. Windows are rebuilt automatically when a monitor is connected or disconnected, the resolution changes,
  Explorer restarts, or the WebView2 browser process dies.
- Rendering pauses per monitor when a maximized or fullscreen window covers that monitor, and on every monitor while the
  session is locked, the screen is off, or the PC is asleep.
- Frame rates are set per layer: the full-screen WebGL layer is redrawn at the configured FPS only during transitions or
  layout moves, and otherwise at 24 FPS while playing (10 FPS when paused or on background-only monitors).
- Audio is analyzed only as much as the screen uses it: 30 Hz while the waveform is visible, 10 Hz for the subtle background
  breathing, and not at all when paused, hidden, or covered — after 10 s of that the capture itself is released.
- Lyrics are shown only when a synced version matches the track length within ±3 s. When nothing matches, the log lists the
  closest candidates (`near miss …`) so the matching rules in `LyricsMatcher.cs` can be tuned with evidence.
