namespace SpotifyWallpaper;

// 작업표시줄 트레이 아이콘과 메뉴
internal sealed class TrayMenu : IDisposable
{
    const int OffsetStepMs = 100, OffsetLimitMs = 5000;

    readonly Settings _settings;
    readonly DisplayManager _displays;
    readonly bool _windowed;
    readonly NotifyIcon _icon;
    readonly Icon _appIcon;
    readonly UpdateService _updates;
    readonly ToolStripMenuItem _lyrics, _sync, _syncInfo, _hiRes, _fps30, _fps60, _monitor, _hideIdle, _hotkey, _autostart, _update;
    readonly ToolStripSeparator _updateSeparator;
    readonly Font _updateNormal, _updateBold;

    public event Action? ToggleLyrics;
    public event Action? SettingsChanged;      // 고해상도 아트, FPS, 가사 싱크, 유휴 숨김
    public event Action<Screen>? MonitorSelected;
    public event Action? HotkeyChangeRequested;
    public event Action? ExitRequested;
    public event Action? ApplyUpdateRequested;

    // 단축키가 설정돼 있지만 등록에 실패했는지 (다른 앱이 쓰는 중)
    public bool HotkeyFailed { get; set; }

    public TrayMenu(Settings settings, DisplayManager displays, UpdateService updates, bool windowed)
    {
        _settings = settings;
        _displays = displays;
        _updates = updates;
        _windowed = windowed;

        _lyrics = new ToolStripMenuItem("가사 표시", null, (_, _) => ToggleLyrics?.Invoke());

        // 가사 싱크: 누를 때마다 0.1초씩. 여러 번 누르기 쉽게 메뉴가 닫히지 않게 한다
        _syncInfo = new ToolStripMenuItem { Enabled = false };
        _sync = new ToolStripMenuItem("가사 싱크");
        _sync.DropDownItems.AddRange(new ToolStripItem[]
        {
            _syncInfo,
            new ToolStripSeparator(),
            new ToolStripMenuItem("가사를 더 빨리  (+0.1초)", null, (_, _) => NudgeOffset(+OffsetStepMs)),
            new ToolStripMenuItem("가사를 더 늦게  (−0.1초)", null, (_, _) => NudgeOffset(-OffsetStepMs)),
            new ToolStripMenuItem("초기화", null, (_, _) => NudgeOffset(-_settings.LyricsOffsetMs)),
        });
        _sync.DropDown.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
        };

        _monitor = new ToolStripMenuItem("앨범 표시 모니터");
        _hiRes = new ToolStripMenuItem("고해상도 앨범아트 (iTunes·Deezer 검색)", null, (_, _) => Change(s => s.HiResArtwork = !s.HiResArtwork));
        _fps30 = new ToolStripMenuItem("30 FPS", null, (_, _) => Change(s => s.Fps = 30));
        _fps60 = new ToolStripMenuItem("60 FPS", null, (_, _) => Change(s => s.Fps = 60));
        var fps = new ToolStripMenuItem("프레임 제한");
        fps.DropDownItems.AddRange(new ToolStripItem[] { _fps30, _fps60 });

        _hideIdle = new ToolStripMenuItem("Spotify가 꺼져 있을 땐 원래 배경화면 보이기", null, (_, _) => Change(s => s.HideWhenIdle = !s.HideWhenIdle));
        _hotkey = new ToolStripMenuItem("단축키 설정…", null, (_, _) => HotkeyChangeRequested?.Invoke());
        _autostart = new ToolStripMenuItem("Windows 시작 시 실행", null, (_, _) => AutoStart.Set(!AutoStart.IsEnabled));

        // 설치본에서만 보임: 평소엔 "업데이트 확인", 새 버전을 내려받아 두었으면 "설치 후 다시 시작"
        _update = new ToolStripMenuItem("업데이트 확인", null, (_, _) =>
        {
            if (_updates.ReadyVersion != null) ApplyUpdateRequested?.Invoke();
            else _ = _updates.CheckAsync(manual: true);
        });
        _updateSeparator = new ToolStripSeparator();
        _updateNormal = _update.Font;
        _updateBold = new Font(_updateNormal, FontStyle.Bold);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _lyrics, _sync, _monitor, _hiRes, fps,
            new ToolStripSeparator(),
            _hideIdle, _hotkey, _autostart,
            new ToolStripSeparator(),
            new ToolStripMenuItem("새로고침", null, (_, _) => { foreach (var d in _displays.Displays) d.Form.Reload(); }),
            new ToolStripMenuItem("개발자 도구", null, (_, _) => _displays.Main?.Form.OpenDevTools()),
            new ToolStripMenuItem("로그 폴더 열기", null, (_, _) => OpenLogFolder()),
            _updateSeparator, _update,
            new ToolStripSeparator(),
            new ToolStripMenuItem($"종료  (v{Program.Version})", null, (_, _) => ExitRequested?.Invoke()),
        });
        // 메뉴를 열 때마다 현재 상태(모니터 목록 포함)로 갱신
        menu.Opening += (_, _) => Refresh();

        _appIcon = LoadIcon();
        _icon = new NotifyIcon { Icon = _appIcon, Text = "Spotify Wallpaper", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => ToggleLyrics?.Invoke();
        // 업데이트 알림 풍선을 누르면 바로 설치
        _icon.BalloonTipClicked += (_, _) => { if (_updates.ReadyVersion != null) ApplyUpdateRequested?.Invoke(); };
        _updates.StateChanged += Refresh;
        _updates.Notice += (title, text) => _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
        Refresh();
    }

    void Change(Action<Settings> change)
    {
        change(_settings);
        SettingsChanged?.Invoke();
    }

    void NudgeOffset(int deltaMs)
    {
        int next = Math.Clamp(_settings.LyricsOffsetMs + deltaMs, -OffsetLimitMs, OffsetLimitMs);
        if (next == _settings.LyricsOffsetMs) return;
        Change(s => s.LyricsOffsetMs = next);
    }

    public void Refresh()
    {
        bool hasHotkey = Hotkey.TryParse(_settings.Hotkey, out var hk);
        _lyrics.Checked = _settings.Lyrics;
        _lyrics.ShortcutKeyDisplayString = hasHotkey && !HotkeyFailed ? hk.ToString() : "";
        _hotkey.Text = "단축키 설정…  (" + (!hasHotkey ? "사용 안 함" : HotkeyFailed ? $"{hk} · 다른 앱이 사용 중" : hk.ToString()) + ")";

        int off = _settings.LyricsOffsetMs;
        _syncInfo.Text = off == 0 ? "보정 없음" : $"현재: 가사 {Math.Abs(off) / 1000.0:0.0}초 {(off > 0 ? "빠르게" : "늦게")}";
        _sync.Text = off == 0 ? "가사 싱크" : $"가사 싱크  ({(off > 0 ? "+" : "−")}{Math.Abs(off) / 1000.0:0.0}초)";

        _hiRes.Checked = _settings.HiResArtwork;
        _fps30.Checked = _settings.Fps == 30;
        _fps60.Checked = _settings.Fps == 60;
        _hideIdle.Checked = _settings.HideWhenIdle;
        _hideIdle.Enabled = !_windowed;
        _autostart.Checked = AutoStart.IsEnabled;
        RefreshUpdate();
        RefreshMonitors();
    }

    void RefreshUpdate()
    {
        _update.Visible = _updateSeparator.Visible = _updates.IsAvailable;
        string? ready = _updates.ReadyVersion;
        _update.Text = ready != null ? $"새 버전 설치 후 다시 시작  (v{ready})"
                     : _updates.Checking ? "업데이트 확인 중…"
                     : "업데이트 확인";
        _update.Enabled = ready != null || !_updates.Checking;
        _update.Font = ready != null ? _updateBold : _updateNormal;
    }

    void RefreshMonitors()
    {
        _monitor.DropDownItems.Clear();
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToArray();
        string mainName = _displays.MainScreen().DeviceName; // 창이 숨어 있어도 선택된 모니터는 표시
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            string pos = screens.Length == 1 ? "" : i == 0 ? "왼쪽" : i == screens.Length - 1 ? "오른쪽" : "가운데";
            string label = $"모니터 {i + 1}  ·  {s.Bounds.Width}×{s.Bounds.Height}" +
                           (pos.Length > 0 ? $"  ·  {pos}" : "") + (s.Primary ? "  ·  주 모니터" : "");
            var item = new ToolStripMenuItem(label) { Checked = s.DeviceName == mainName };
            item.Click += (_, _) => MonitorSelected?.Invoke(s);
            _monitor.DropDownItems.Add(item);
        }
        _monitor.Enabled = !_windowed && screens.Length > 1;
    }

    static void OpenLogFolder()
    {
        Directory.CreateDirectory(AppPaths.Data);
        System.Diagnostics.Process.Start("explorer.exe", AppPaths.Data);
    }

    // 실행 파일에 넣어 둔 app.ico (없으면 기본 아이콘)
    static Icon LoadIcon()
    {
        try
        {
            using var stream = typeof(TrayMenu).Assembly.GetManifestResourceStream("app.ico");
            if (stream != null) return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception ex) { Log.Write("tray icon: " + ex.Message); }
        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
        _updateBold.Dispose();
    }
}
