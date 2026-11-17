# Spotify Wallpaper

A live desktop wallpaper that reacts to the song playing in Spotify (Windows 10/11).
It shows the album art in its original colors over a dark blurred background, and can show synced lyrics.
When Spotify is closed it gets out of the way and your normal wallpaper is shown.

## 설치 (Install)

1. [최신 Release](https://github.com/danyoon0760/spotify-wallpaper/releases/latest)에서 `SpotifyWallpaper.App-win-Setup.exe`를 받아 실행합니다.
2. "Windows의 PC 보호" 창이 뜨면 **추가 정보 → 실행**을 누릅니다. 코드 서명을 하지 않은 프로그램이라 처음 한 번 뜹니다.
3. 설치가 끝나면 바로 켜집니다. 작업 표시줄 오른쪽 **트레이 아이콘**에서 설정하고, 필요하면 "Windows 시작 시 실행"을 켜 두세요.

- 필요한 것: Windows 10 (2004 이상) 또는 11, x64, Spotify 데스크톱 앱. .NET은 앱에 들어 있어 따로 설치할 필요가 없고,
  WebView2 런타임이 없는 PC라면 설치 프로그램이 함께 설치합니다.
- 새 버전이 나오면 앱이 스스로 내려받아 트레이에서 알려 줍니다. 트레이 메뉴의 "새 버전 설치 후 다시 시작"을 누르거나,
  다음에 앱이 켜질 때 자동으로 적용됩니다.
- 설치하지 않고 쓰려면 같은 Release의 `SpotifyWallpaper.App-win-Portable.zip`을 풀어 `SpotifyWallpaper.exe`를 실행하세요.
- 제거: Windows 설정 → 앱 → 설치된 앱 → Spotify Wallpaper. 자동 시작 등록과 캐시도 함께 지워집니다
  (설정과 로그가 있는 `%APPDATA%\SpotifyWallpaper`는 남습니다).

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
| Show the original wallpaper when Spotify is closed | Hides the windows 30 s after Spotify closes (paused or stopped is fine as long as Spotify is open); they come back when Spotify starts again |
| Hotkey… | Press a new combination for "Show lyrics", or turn the hotkey off (default `Ctrl+Alt+L`) |
| Start with Windows | Registers it in HKCU Run |
| Reload / Developer tools / Open log folder | For development |
| Check for updates / Install new version and restart | Installed copies only; checks GitHub Releases 1 min after start and every 6 h |

Files it keeps:

| Path | Contents |
|---|---|
| `%APPDATA%\SpotifyWallpaper\settings.json` | Settings |
| `%APPDATA%\SpotifyWallpaper\log.txt` (+ `log.old.txt`) | Log, rotated at 512 KB |
| `%LOCALAPPDATA%\SpotifyWallpaper\art-cache` | Album art (up to 400 hi-res images, oldest removed first) |
| `%LOCALAPPDATA%\SpotifyWallpaper\lyrics-cache` | Lyrics search results |
| `%LOCALAPPDATA%\SpotifyWallpaper\WebView2` | Browser profile |
| `%LOCALAPPDATA%\SpotifyWallpaper.App` | The installed app itself (Velopack), when installed with Setup |

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
  UpdateService.cs           self-update from GitHub Releases (Velopack)
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
.github/workflows/release.yml   tag v* → test, build, Setup.exe + portable zip, GitHub Release
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

### Releasing a new version

Push a tag that looks like `v1.2.3`. GitHub Actions runs the tests, builds a self-contained package with
[Velopack](https://velopack.io), and publishes `Setup.exe`, the portable zip and the update packages as a GitHub Release.
Installed copies pick it up on their own.

```
git tag v1.0.1
git push origin v1.0.1
```

The version comes from the tag (the `<Version>` in the csproj is only used for local builds).
If you update the `Velopack` package, change `VPK_VERSION` in the workflow to the same version.

Running with the `WALLPAPER_DEBUG_PORT=9229` environment variable opens a remote debugging port, so you can use
`node dev/cdp-eval.mjs "<JS expression>"` and `node dev/cdp-eval.mjs --shot out.png` (`CDP_PAGE=bg` selects a background-only page).

Other scripts: `node dev/fetch-fonts.mjs` re-downloads the bundled fonts (only needed to change versions),
`dev\make-icon.ps1` redraws `host\app.ico`.

## License

MIT — see [LICENSE](LICENSE). The bundled fonts are under the SIL Open Font License (`web/fonts/licenses`).

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
