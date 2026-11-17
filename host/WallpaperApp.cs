using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
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
    // Spotify가 꺼진 채로 이만큼 지나면 창을 숨겨 원래 배경화면을 보여 준다
    // (일시정지·정지는 Spotify가 켜져 있는 한 숨기지 않음. 잠깐 재시작할 때 깜빡이지 않도록 여유를 둠)
    const int SpotifyOffHideMs = 30_000;

    readonly Settings _settings = Settings.Load();
    readonly SynchronizationContext _ui;
    readonly ArtStore _art = new();
    readonly MediaWatcher _media = new();
    readonly AudioAnalyzer _audio = new();
    readonly LyricsService _lyrics = new();
    readonly ArtworkService _artwork;
    readonly DisplayManager _displays;
    readonly TrayMenu _tray;
    readonly MessageWindow _msgWindow;
    readonly System.Windows.Forms.Timer _idleTimer = new();
    readonly bool _windowed;
    readonly EventWaitHandle _exitSignal;
    readonly RegisteredWaitHandle _exitWait;
    IntPtr _displayStateNotify;
    bool _hotkeyActive;
    bool _locked, _displayOff, _sleeping;
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
        // 다른 스레드에서 온 이벤트를 UI 스레드로 넘길 통로 (창을 하나도 안 만든 채 시작할 수 있으므로 직접 준비)
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;

        _artwork = new ArtworkService(_art);
        _ = Task.Run(_art.Prune);

        _displays = new DisplayManager(windowed, _art, () => _settings.MainMonitor);
        _displays.PageReady += OnPageReady;
        _displays.WindowClosed += ExitApp;
        _displays.AudioRateChanged += _audio.SetRate;
        // Spotify가 꺼진 채로 켜졌다면 원래 배경화면을 그대로 두고, Spotify가 켜지면 나타난다
        _displays.Start(asleep: _settings.HideWhenIdle);

        _tray = new TrayMenu(_settings, _displays, windowed);
        _tray.ToggleLyrics += ToggleLyrics;
        _tray.SettingsChanged += ApplySettings;
        _tray.MonitorSelected += SetMainMonitor;
        _tray.HotkeyChangeRequested += ChangeHotkey;
        _tray.ExitRequested += ExitApp;

        _msgWindow = new MessageWindow(
            onHotkey: id => { if (id == HotkeyLyrics) ToggleLyrics(); },
            onTaskbarCreated: () => { if (!_windowed) _displays.RecreateSoon(2000); }, // Explorer 재시작 → 바탕화면에 다시 붙임
            onDisplayPower: on => { _displayOff = !on; UpdateSuspended(); });
        ApplyHotkey();

        // 잠금·모니터 꺼짐·절전: 아무도 보지 않는 동안엔 그리지 않는다
        var displayState = GUID_CONSOLE_DISPLAY_STATE;
        _displayStateNotify = RegisterPowerSettingNotification(_msgWindow.Handle, ref displayState, 0);
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            _displays.Sleep();
        };

        // 다른 프로세스(SpotifyWallpaper.exe --exit)가 보낸 종료 요청 → 트레이의 "종료"와 똑같이 정리하고 끝낸다
        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitSignalName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitSignal, (_, _) =>
        {
            Log.Write("exit requested by --exit");
            _ui.Post(_ => ExitApp(), null);
        }, null, Timeout.Infinite, executeOnlyOnce: true);

        _media.TrackChanged += OnTrack;
        _media.PlaybackChanged += OnPlayback;
        _media.TimelineChanged += (pos, dur) => Send(_timelineJson = Ser(new { type = "timeline", position = Math.Round(pos), duration = Math.Round(dur) }));
        _media.SessionPresentChanged += present =>
        {
            _audio.SpotifyPresent = present;
            _ui.Post(_ => UpdateIdle(), null);
        };
        // 곡 정보가 처음 들어오거나 사라질 때도 다시 판단 (세션만 있고 곡이 없으면 아직 꺼진 것으로 봄)
        _media.TrackChanged += _ => _ui.Post(_ => UpdateIdle(), null);
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

    // ───────────── 재생 상태 / 유휴 숨김 ─────────────

    void OnPlayback(string state)
    {
        Send(_playbackJson = Ser(new { type = "playback", state }));
    }

    // Spotify가 켜져 있으면(재생·일시정지·정지 무관) 보이고, 꺼진 채로 시간이 지나면 숨는다
    void UpdateIdle()
    {
        if (_exiting) return;
        bool spotifyOn = _media.SessionPresent && _media.Current != null;
        if (_windowed || !_settings.HideWhenIdle || spotifyOn)
        {
            _idleTimer.Stop();
            _displays.Wake();
            return;
        }
        // 이미 숨어 있거나 숨기기 타이머가 돌고 있으면 그대로 (타이머를 다시 처음부터 세지 않음)
        if (_displays.Asleep || _idleTimer.Enabled) return;
        _idleTimer.Interval = SpotifyOffHideMs;
        _idleTimer.Start();
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _locked = true;
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _locked = false;
        else return;
        UpdateSuspended();
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _sleeping = true;
        else if (e.Mode == PowerModes.Resume) _sleeping = false;
        else return;
        UpdateSuspended();
    }

    void UpdateSuspended()
    {
        if (!_exiting) _displays.Suspended = _locked || _displayOff || _sleeping;
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
        if (sendThumb)
        {
            string id = t.ThumbHash.ToLowerInvariant(); // SHA-1 16진수 → 저장소 파일 이름으로 그대로 사용
            string? file = _art.Find(ArtStore.Thumb, id) ?? _art.Save(ArtStore.Thumb, id, t.Thumbnail!);
            if (file != null) SendArt(t.Key, file, hiRes: false);
        }
    }

    // 화면에는 이미지 자체가 아니라 저장소 주소만 보낸다
    void SendArt(string key, string file, bool hiRes)
    {
        string json = Ser(new { type = "art", key, src = ArtStore.Url(file) });
        lock (_stateLock)
        {
            if (key != _currentKey) return;
            if (!hiRes && _hiResShown) return; // 고해상도가 먼저 도착했으면 썸네일로 되돌리지 않음
            if (hiRes) _hiResShown = true;
            _artJson = json;
        }
        Send(json);
    }

    async Task FetchArtworkAsync(TrackInfo t, CancellationToken ct)
    {
        try
        {
            string? file = await _artwork.FindAsync(t, ct);
            if (file == null || ct.IsCancellationRequested) return;
            SendArt(t.Key, file, hiRes: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write("artwork fetch: " + ex.Message); }
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
        UpdateIdle(); // "원래 배경화면 보이기"를 켜고 껐을 수 있음
    }

    void SetMainMonitor(Screen screen)
    {
        if (_displays.MainScreen().DeviceName == screen.DeviceName) return;
        _settings.MainMonitor = screen.DeviceName;
        _settings.Save();
        _displays.Recreate();
    }

    // ───────────── 단축키 ─────────────

    void ApplyHotkey()
    {
        UnregisterHotKey(_msgWindow.Handle, HotkeyLyrics);
        _hotkeyActive = false;
        bool wanted = Hotkey.TryParse(_settings.Hotkey, out var hk);
        if (wanted)
        {
            _hotkeyActive = RegisterHotKey(_msgWindow.Handle, HotkeyLyrics, hk.Modifiers | MOD_NOREPEAT, (uint)hk.Key);
            if (!_hotkeyActive) Log.Write($"hotkey {hk} registration failed (probably used by another app)");
        }
        else if (!string.IsNullOrWhiteSpace(_settings.Hotkey))
            Log.Write($"hotkey setting '{_settings.Hotkey}' is not valid → disabled");
        _tray.HotkeyFailed = wanted && !_hotkeyActive;
    }

    void ChangeHotkey()
    {
        // 지금 쓰는 조합도 다시 눌러 볼 수 있게 잠시 해제
        UnregisterHotKey(_msgWindow.Handle, HotkeyLyrics);
        using (var dialog = new HotkeyDialog(_settings.Hotkey))
        {
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                _settings.Hotkey = dialog.Result;
                _settings.Save();
            }
        }
        ApplyHotkey();
        _tray.Refresh();
        if (_tray.HotkeyFailed)
            MessageBox.Show($"{_settings.Hotkey} 조합은 다른 프로그램이 이미 쓰고 있어 등록하지 못했습니다.\n다른 조합을 골라 주세요.",
                "Spotify Wallpaper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ───────────── 종료 ─────────────

    void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _exitWait.Unregister(null);
        _exitSignal.Dispose();
        _idleTimer.Dispose();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_displayStateNotify != IntPtr.Zero) UnregisterPowerSettingNotification(_displayStateNotify);
        UnregisterHotKey(_msgWindow.Handle, HotkeyLyrics);
        _audio.Dispose();
        _media.Dispose();
        _tray.Dispose();
        _displays.Dispose();
        if (!_windowed) DesktopHost.RefreshWallpaper();
        _msgWindow.DestroyHandle();
        ExitThread();
    }

    // 단축키, Explorer 재시작 브로드캐스트, 모니터 전원 알림을 받는 숨은 창
    sealed class MessageWindow : NativeWindow
    {
        readonly Action<int> _onHotkey;
        readonly Action _onTaskbarCreated;
        readonly Action<bool> _onDisplayPower;
        readonly int _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

        public MessageWindow(Action<int> onHotkey, Action onTaskbarCreated, Action<bool> onDisplayPower)
        {
            _onHotkey = onHotkey;
            _onTaskbarCreated = onTaskbarCreated;
            _onDisplayPower = onDisplayPower;
            CreateHandle(new CreateParams { Caption = "SpotifyWallpaper.Messages" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY) _onHotkey(m.WParam.ToInt32());
            else if (m.Msg == _taskbarCreatedMsg) _onTaskbarCreated();
            else if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt64() == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                // POWERBROADCAST_SETTING { GUID PowerSetting; DWORD DataLength; BYTE Data[] }
                if (Marshal.PtrToStructure<Guid>(m.LParam) == GUID_CONSOLE_DISPLAY_STATE)
                    _onDisplayPower(Marshal.ReadInt32(m.LParam, 20) != 0); // 0 = 꺼짐, 1 = 켜짐, 2 = 어두워짐
            }
            base.WndProc(ref m);
        }
    }
}
