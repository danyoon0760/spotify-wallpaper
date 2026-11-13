using Microsoft.Win32;
using static SpotifyWallpaper.Native;

namespace SpotifyWallpaper;

// 모니터마다 배경화면 창을 하나씩 관리한다.
//  - 선택된 모니터(Main)는 앨범·가사, 나머지는 흐린 배경만
//  - 1초마다: 바탕화면 부모 생존 확인, Z순서 보정, 최대화/전체화면 창에 가려진 모니터는 렌더링 중지
//  - 모니터 연결/해제·해상도 변경·Explorer 재시작 시 창을 다시 만든다
internal sealed class DisplayManager : IDisposable
{
    public sealed class Display
    {
        public required Screen Screen { get; init; }
        public required bool Main { get; init; }
        public required WallpaperForm Form { get; init; }
        public DesktopHost Desk { get; set; } = new();
        public bool Hidden { get; set; }
    }

    readonly bool _windowed;
    readonly Func<string?> _mainMonitor;
    readonly System.Windows.Forms.Timer _watch = new() { Interval = 1000 };
    System.Windows.Forms.Timer? _rebuildTimer;
    List<Display> _displays = new();
    bool _recreating;
    bool? _lastMainHidden; // 마지막으로 알린 값 (창을 다시 만들어도 어긋나지 않게)
    bool _disposed;

    // 페이지가 (재)로드되어 상태를 다시 보내야 할 때
    public event Action<Display>? PageReady;
    // 일반 창 모드(--window)에서 사용자가 창을 닫았을 때
    public event Action? WindowClosed;
    // 앨범 모니터가 가려졌는지 바뀌었을 때 (오디오 분석 일시정지용)
    public event Action<bool>? MainHiddenChanged;

    public IReadOnlyList<Display> Displays => _displays;
    public Display? Main => _displays.FirstOrDefault(d => d.Main);

    public DisplayManager(bool windowed, Func<string?> mainMonitor)
    {
        _windowed = windowed;
        _mainMonitor = mainMonitor;
        _watch.Tick += (_, _) => Watch();
        if (!windowed) SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void Start()
    {
        Create();
        _watch.Start();
    }

    public void Post(string json)
    {
        foreach (var d in _displays) d.Form.Post(json);
    }

    // 오디오는 앨범 모니터(파형·배경 반응)만 쓰므로, 그 창이 보일 때만 받는다
    public WallpaperForm? VisibleMainForm => Main is { Hidden: false } m && m.Form.IsReady ? m.Form : null;

    public Screen MainScreen()
    {
        var screens = Screen.AllScreens;
        return screens.FirstOrDefault(s => s.DeviceName == _mainMonitor()) ?? Screen.PrimaryScreen ?? screens[0];
    }

    public void Recreate()
    {
        if (_disposed) return;
        _recreating = true;
        foreach (var d in _displays)
            try { if (!d.Form.IsDisposed) d.Form.Close(); } catch { }
        _recreating = false;
        Create();
    }

    // 여러 변경이 연달아 올 때 한 번만 다시 만들도록 지연
    public void RecreateSoon(int delayMs)
    {
        if (_disposed || _windowed) return;
        _rebuildTimer?.Dispose();
        _rebuildTimer = new System.Windows.Forms.Timer { Interval = delayMs };
        _rebuildTimer.Tick += (_, _) =>
        {
            _rebuildTimer?.Dispose();
            _rebuildTimer = null;
            Recreate();
        };
        _rebuildTimer.Start();
    }

    void Create()
    {
        var main = MainScreen();
        var screens = _windowed ? new[] { main } : Screen.AllScreens;
        _displays = screens.Select(s => CreateDisplay(s, s.DeviceName == main.DeviceName)).ToList();
        Log.Write("displays: " + string.Join(", ", _displays.Select(d => $"{d.Screen.DeviceName} {d.Screen.Bounds} {(d.Main ? "main" : "background")}")));
    }

    Display CreateDisplay(Screen screen, bool main)
    {
        var bounds = screen.Bounds;
        var form = new WallpaperForm(_windowed, bounds, backgroundOnly: !main);
        var d = new Display { Screen = screen, Main = main, Form = form };
        form.Ready += () => PageReady?.Invoke(d);
        form.FormClosed += (_, _) =>
        {
            if (_windowed && !_recreating && !_disposed) WindowClosed?.Invoke();
        };
        form.Shown += async (_, _) =>
        {
            if (!_windowed)
            {
                d.Desk = new DesktopHost();
                if (!d.Desk.Attach(form.Handle, bounds))
                    Log.Write($"desktop attach failed ({screen.DeviceName})");
            }
            try { await form.InitAsync(); }
            catch (Exception ex)
            {
                Log.Write("webview init: " + ex);
                if (main) MessageBox.Show("WebView2 초기화에 실패했습니다.\n" + ex.Message, "Spotify Wallpaper");
            }
        };
        form.Show();
        return d;
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Log.Write("display settings changed → recreate");
        RecreateSoon(1500);
    }

    void Watch()
    {
        if (_windowed || _displays.Count == 0) return;

        // 붙어 있던 부모(Explorer 바탕화면)가 사라졌으면 다시 만든다 (아직 안 붙은 창은 제외)
        if (_displays.Any(d => d.Form.IsDisposed || (d.Desk.Parent != IntPtr.Zero && !d.Desk.IsParentAlive)))
        {
            Log.Write("desktop parent lost → recreate");
            Recreate();
            return;
        }

        // 24H2+ 구조에서만: 배경화면 창들이 아이콘 바로 뒤에 모여 있는지 (깨졌을 때만 고침)
        var raised = _displays.Where(d => d.Desk.Raised).ToList();
        if (raised.Count > 0)
            DesktopHost.EnsureBelowIcons(raised[0].Desk.DefView, raised.Select(d => d.Form.Handle).ToList());
        var covered = CoveredMonitor();
        foreach (var d in _displays)
        {
            bool hidden = covered is { } m && m == d.Screen.Bounds;
            if (hidden == d.Hidden) continue;
            d.Hidden = hidden;
            d.Form.Post($"{{\"type\":\"visibility\",\"hidden\":{(hidden ? "true" : "false")}}}");
            d.Form.SetMemoryLow(hidden || !d.Main);
        }
        bool mainHidden = Main?.Hidden ?? true;
        if (mainHidden != _lastMainHidden) { _lastMainHidden = mainHidden; MainHiddenChanged?.Invoke(mainHidden); }
    }

    // 맨 앞 창이 최대화/전체화면으로 덮고 있는 모니터 영역 (없으면 null)
    Rectangle? CoveredMonitor()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || _displays.Any(d => d.Form.Handle == fg)) return null;
        if (ClassOf(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
        if (!IsWindowVisible(fg) || IsCloaked(fg)) return null;

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST), ref info)) return null;
        var m = info.rcMonitor;
        var rect = new Rectangle(m.Left, m.Top, m.Width, m.Height);

        if (IsZoomed(fg)) return rect;
        GetWindowRect(fg, out RECT r);
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom ? rect : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_windowed) SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _watch.Dispose();
        _rebuildTimer?.Dispose();
        foreach (var d in _displays)
            try { if (!d.Form.IsDisposed) d.Form.Close(); } catch { }
    }
}
