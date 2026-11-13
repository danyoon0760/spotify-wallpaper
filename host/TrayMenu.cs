using System.Drawing.Drawing2D;

namespace SpotifyWallpaper;

// 작업표시줄 트레이 아이콘과 메뉴
internal sealed class TrayMenu : IDisposable
{
    readonly Settings _settings;
    readonly DisplayManager _displays;
    readonly bool _windowed;
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _lyrics, _hiRes, _fps30, _fps60, _monitor, _autostart;

    public event Action? ToggleLyrics;
    public event Action? SettingsChanged;      // 고해상도 아트, FPS
    public event Action<Screen>? MonitorSelected;
    public event Action? ExitRequested;

    public TrayMenu(Settings settings, DisplayManager displays, bool windowed)
    {
        _settings = settings;
        _displays = displays;
        _windowed = windowed;

        _lyrics = new ToolStripMenuItem("가사 표시", null, (_, _) => ToggleLyrics?.Invoke()) { ShortcutKeyDisplayString = "Ctrl+Alt+L" };
        _monitor = new ToolStripMenuItem("앨범 표시 모니터");
        _hiRes = new ToolStripMenuItem("고해상도 앨범아트 (iTunes 검색)", null, (_, _) => Change(s => s.HiResArtwork = !s.HiResArtwork));
        _fps30 = new ToolStripMenuItem("30 FPS", null, (_, _) => Change(s => s.Fps = 30));
        _fps60 = new ToolStripMenuItem("60 FPS", null, (_, _) => Change(s => s.Fps = 60));
        var fps = new ToolStripMenuItem("프레임 제한");
        fps.DropDownItems.AddRange(new ToolStripItem[] { _fps30, _fps60 });
        _autostart = new ToolStripMenuItem("Windows 시작 시 실행", null, (_, _) => AutoStart.Set(!AutoStart.IsEnabled));

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _lyrics, _monitor, _hiRes, fps,
            new ToolStripSeparator(),
            _autostart,
            new ToolStripMenuItem("새로고침", null, (_, _) => { foreach (var d in _displays.Displays) d.Form.Reload(); }),
            new ToolStripMenuItem("개발자 도구", null, (_, _) => _displays.Main?.Form.OpenDevTools()),
            new ToolStripMenuItem("로그 폴더 열기", null, (_, _) => OpenLogFolder()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("종료", null, (_, _) => ExitRequested?.Invoke()),
        });
        // 메뉴를 열 때마다 현재 상태(모니터 목록 포함)로 갱신
        menu.Opening += (_, _) => Refresh();

        _icon = new NotifyIcon { Icon = MakeIcon(), Text = "Spotify Wallpaper", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => ToggleLyrics?.Invoke();
        Refresh();
    }

    void Change(Action<Settings> change)
    {
        change(_settings);
        SettingsChanged?.Invoke();
    }

    public void Refresh()
    {
        _lyrics.Checked = _settings.Lyrics;
        _hiRes.Checked = _settings.HiResArtwork;
        _fps30.Checked = _settings.Fps == 30;
        _fps60.Checked = _settings.Fps == 60;
        _autostart.Checked = AutoStart.IsEnabled;
        RefreshMonitors();
    }

    void RefreshMonitors()
    {
        _monitor.DropDownItems.Clear();
        var screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToArray();
        string? mainName = _displays.Main?.Screen.DeviceName;
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
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyWallpaper");
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.FillEllipse(Brushes.Black, 1, 1, 30, 30);
            using var ring = new Pen(Color.FromArgb(242, 239, 233), 2.5f);
            g.DrawEllipse(ring, 2, 2, 28, 28);
            using var groove = new Pen(Color.FromArgb(150, 242, 239, 233), 1.2f);
            for (int y = 9; y <= 23; y += 4) g.DrawLine(groove, 8, y, 24, y);
            using var dot = new SolidBrush(Color.FromArgb(230, 70, 90));
            g.FillEllipse(dot, 12, 12, 8, 8);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
