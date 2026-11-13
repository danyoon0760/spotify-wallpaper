using System.Globalization;
using System.Text;
using System.Text.Json;
using static SpotifyWallpaper.Native;

namespace SpotifyWallpaper;

// 앱 조립: 미디어(SMTC) → 앨범아트·가사 → 모든 모니터 창으로 전달
//  DisplayManager — 모니터별 창 / TrayMenu — 트레이 / MediaWatcher·AudioAnalyzer — 입력
public sealed class WallpaperApp : ApplicationContext
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    // 서버 오류로 판단을 못 하면 "가사 없음"으로 처리하지 않고 뒤에서 계속 다시 시도
    static readonly int[] LyricsRetryDelays = { 4000, 10000, 30000, 60000 };
    const int HotkeyLyrics = 1;

    readonly Settings _settings = Settings.Load();
    readonly MediaWatcher _media = new();
    readonly AudioAnalyzer _audio = new();
    readonly LyricsService _lyrics = new();
    readonly ArtworkService _artwork = new();
    readonly DisplayManager _displays;
    readonly TrayMenu _tray;
    readonly MessageWindow _msgWindow;
    readonly bool _windowed;
    bool _exiting;

    // 현재 곡 상태. 페이지가 (재)로드되면 그대로 다시 보낸다
    readonly object _stateLock = new();
    string? _currentKey;
    // 썸네일 추적: Spotify는 곡이 바뀐 직후 "이전 곡 썸네일"을 잠깐 그대로 보내므로 걸러야 한다
    string? _lastThumbHash;   // SMTC에서 마지막으로 본 썸네일 (곡 무관)
    string? _staleThumbHash;  // 곡이 바뀌는 순간의 썸네일 = 이전 곡 것
    string? _sentThumbHash;   // 현재 곡으로 이미 보낸 썸네일
    bool _hiResShown;
    string? _lyricsFetchedFor;
    string? _trackJson, _artJson, _lyricsJson, _playbackJson, _timelineJson;
    CancellationTokenSource? _trackCts;

    public WallpaperApp(bool windowed)
    {
        _windowed = windowed;
        _displays = new DisplayManager(windowed, () => _settings.MainMonitor);
        _displays.PageReady += OnPageReady;
        _displays.WindowClosed += ExitApp;
        _displays.MainHiddenChanged += hidden => _audio.Paused = hidden;
        _displays.Start();

        _tray = new TrayMenu(_settings, _displays, windowed);
        _tray.ToggleLyrics += ToggleLyrics;
        _tray.SettingsChanged += ApplySettings;
        _tray.MonitorSelected += SetMainMonitor;
        _tray.ExitRequested += ExitApp;

        _msgWindow = new MessageWindow(
            onHotkey: id => { if (id == HotkeyLyrics) ToggleLyrics(); },
            onTaskbarCreated: () => _displays.RecreateSoon(2000)); // Explorer 재시작 → 바탕화면에 다시 붙임
        if (!RegisterHotKey(_msgWindow.Handle, HotkeyLyrics, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.L))
            Log.Write("hotkey Ctrl+Alt+L registration failed");

        _media.TrackChanged += OnTrack;
        _media.PlaybackChanged += state => Send(_playbackJson = Ser(new { type = "playback", state }));
        _media.TimelineChanged += (pos, dur) => Send(_timelineJson = Ser(new { type = "timeline", position = Math.Round(pos), duration = Math.Round(dur) }));
        _ = StartMediaAsync();

        _audio.Frame += OnAudioFrame;
        _audio.Start();
    }

    async Task StartMediaAsync()
    {
        try { await _media.StartAsync(); }
        catch (Exception ex) { Log.Write("media start: " + ex.Message); }
    }

    static string Ser(object o) => JsonSerializer.Serialize(o, Json);

    void Send(string json) => _displays.Post(json);

    void OnPageReady(DisplayManager.Display d)
    {
        d.Form.Post(Ser(_settings.ToMessage()));
        string?[] state;
        lock (_stateLock) state = new[] { _trackJson, _artJson, _playbackJson, _timelineJson, _lyricsJson };
        foreach (var json in state)
            if (json != null) d.Form.Post(json);
        d.Form.Post(Ser(new { type = "visibility", hidden = d.Hidden }));
    }

    // ───────────── 곡 / 앨범아트 ─────────────

    void OnTrack(TrackInfo? t)
    {
        if (t == null) return;
        CancellationToken token;
        bool isNew, sendThumb;
        lock (_stateLock)
        {
            isNew = t.Key != _currentKey;
            if (isNew)
            {
                _currentKey = t.Key;
                _staleThumbHash = _lastThumbHash;
                _sentThumbHash = null;
                _hiResShown = false;
                _lyricsFetchedFor = null;
                _lyricsJson = null;
                _trackCts?.Cancel();
                _trackCts = new CancellationTokenSource();
                _trackJson = Ser(new { type = "track", key = t.Key, title = t.Title, artist = t.Artist, album = t.Album });
            }
            token = _trackCts!.Token;

            // 이전 곡 썸네일(stale)이거나 이미 보낸 것이면 보내지 않음. 고해상도가 이미 떴으면 썸네일은 불필요
            if (t.Thumbnail != null) _lastThumbHash = t.ThumbHash;
            sendThumb = t.Thumbnail != null && !_hiResShown &&
                        t.ThumbHash != _staleThumbHash && t.ThumbHash != _sentThumbHash;
            if (sendThumb) _sentThumbHash = t.ThumbHash;
        }

        if (isNew)
        {
            Send(_trackJson!);
            if (_settings.HiResArtwork) _ = FetchArtworkAsync(t, token);
            if (_settings.Lyrics) _ = FetchLyricsAsync(t, token);
        }
        if (sendThumb) SendArt(t.Key, t.Thumbnail!);
    }

    void SendArt(string key, byte[] bytes)
    {
        string mime = bytes.Length > 3 && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png" : "image/jpeg";
        string json = Ser(new { type = "art", key, src = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" });
        lock (_stateLock)
        {
            if (key != _currentKey) return;
            _artJson = json;
        }
        Send(json);
    }

    async Task FetchArtworkAsync(TrackInfo t, CancellationToken ct)
    {
        try
        {
            var bytes = await _artwork.FindAsync(t, ct);
            if (bytes == null || ct.IsCancellationRequested) return;
            lock (_stateLock) _hiResShown = true;
            SendArt(t.Key, bytes);
        }
        catch (OperationCanceledException) { }
    }

    // ───────────── 가사 ─────────────

    async Task FetchLyricsAsync(TrackInfo t, CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_lyricsFetchedFor == t.Key) return;
            _lyricsFetchedFor = t.Key;
        }
        try
        {
            double dur = await WaitForTrackDurationAsync(ct);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var res = await _lyrics.GetAsync(t, dur, ct);
                    Log.Write($"lyrics '{t.Title}': {res.Status}, {res.Lines.Count} lines, {sw.ElapsedMilliseconds}ms");
                    SendLyrics(t.Key, res.Status, res.Lines);
                    return;
                }
                catch (LyricsUnavailableException ex)
                {
                    Log.Write($"lyrics '{t.Title}' unavailable (attempt {attempt + 1}): {ex.Message}");
                    // 두 번째 실패부터는 "없음"처럼 보여 주되(빈 대기 방지) 재시도는 계속
                    if (attempt == 1) SendLyrics(t.Key, "error", Array.Empty<LyricLine>());
                    await Task.Delay(LyricsRetryDelays[Math.Min(attempt, LyricsRetryDelays.Length - 1)], ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Write("lyrics fetch: " + ex);
            lock (_stateLock) _lyricsFetchedFor = null;
            SendLyrics(t.Key, "error", Array.Empty<LyricLine>());
        }
    }

    // 곡이 바뀐 직후엔 이전 곡 길이가 남아 있을 수 있으므로, 길이가 바뀔 때까지 최대 2초 기다림
    async Task<double> WaitForTrackDurationAsync(CancellationToken ct)
    {
        double prev = _media.PreviousDuration;
        double dur = _media.GetTimeline().dur;
        for (int i = 0; i < 10 && (dur <= 0 || (prev > 0 && Math.Abs(dur - prev) < 500)); i++)
        {
            await Task.Delay(200, ct);
            dur = _media.GetTimeline().dur;
        }
        return dur;
    }

    void SendLyrics(string key, string status, IReadOnlyList<LyricLine> lines)
    {
        string json = Ser(new { type = "lyrics", key, status, lines = lines.Select(l => new { t = l.T, text = l.Text }) });
        lock (_stateLock)
        {
            if (key != _currentKey) return;
            _lyricsJson = json;
        }
        Send(json);
    }

    // ───────────── 오디오 ─────────────

    readonly StringBuilder _audioSb = new(800);

    void OnAudioFrame(float[] bands)
    {
        var form = _displays.VisibleMainForm;
        if (form == null) return;
        string json;
        lock (_audioSb)
        {
            _audioSb.Clear().Append("{\"type\":\"audio\",\"b\":[");
            for (int i = 0; i < bands.Length; i++)
            {
                if (i > 0) _audioSb.Append(',');
                _audioSb.Append(bands[i].ToString("0.###", CultureInfo.InvariantCulture));
            }
            json = _audioSb.Append("]}").ToString();
        }
        form.Post(json);
    }

    // ───────────── 설정 ─────────────

    void ToggleLyrics()
    {
        _settings.Lyrics = !_settings.Lyrics;
        ApplySettings();
        if (!_settings.Lyrics || _media.Current is not { } t) return;

        string? cached;
        CancellationToken token;
        lock (_stateLock)
        {
            cached = _lyricsJson;
            token = _trackCts?.Token ?? CancellationToken.None;
        }
        if (cached != null) Send(cached);
        else _ = FetchLyricsAsync(t, token);
    }

    void ApplySettings()
    {
        _settings.Save();
        _tray.Refresh();
        Send(Ser(_settings.ToMessage()));
    }

    void SetMainMonitor(Screen screen)
    {
        if (_displays.Main?.Screen.DeviceName == screen.DeviceName) return;
        _settings.MainMonitor = screen.DeviceName;
        _settings.Save();
        _displays.Recreate();
    }

    void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        UnregisterHotKey(_msgWindow.Handle, HotkeyLyrics);
        _audio.Dispose();
        _media.Dispose();
        _tray.Dispose();
        _displays.Dispose();
        if (!_windowed) RefreshDesktopWallpaper();
        _msgWindow.DestroyHandle();
        ExitThread();
    }

    // 붙어 있던 창이 사라진 뒤 바탕화면에 마지막 프레임이 남지 않도록 현재 배경화면을 다시 적용
    static void RefreshDesktopWallpaper()
    {
        var sb = new StringBuilder(520);
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) && sb.Length > 0)
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, sb, 0);
    }

    // 단축키와 Explorer 재시작 브로드캐스트를 받는 숨은 창
    sealed class MessageWindow : NativeWindow
    {
        readonly Action<int> _onHotkey;
        readonly Action _onTaskbarCreated;
        readonly int _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

        public MessageWindow(Action<int> onHotkey, Action onTaskbarCreated)
        {
            _onHotkey = onHotkey;
            _onTaskbarCreated = onTaskbarCreated;
            CreateHandle(new CreateParams { Caption = "SpotifyWallpaper.Messages" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) _onHotkey(m.WParam.ToInt32());
            else if (m.Msg == _taskbarCreatedMsg) _onTaskbarCreated();
            base.WndProc(ref m);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int RegisterWindowMessage(string msg);
    }
}
