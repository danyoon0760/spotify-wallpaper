using Microsoft.Win32;
using static SpotifyWallpaper.Native;

namespace SpotifyWallpaper;

// 모니터마다 배경화면 창을 하나씩 관리한다.
//  - 선택된 모니터(Main)는 앨범·가사, 나머지는 흐린 배경만
//  - 1초마다: 바탕화면 부모 생존 확인, Z순서 보정, 최대화/전체화면 창에 가려진 모니터는 렌더링 중지
//  - 모니터 연결/해제·해상도 변경·Explorer 재시작·WebView2 비정상 종료 시 창을 다시 만든다
//  - Suspended(잠금·화면 꺼짐·절전): 모든 창의 렌더링을 멈춘다
//  - Sleep/Wake(Spotify가 꺼져 있을 때): 창을 숨겨 원래 배경화면이 보이게 하고, 오래 이어지면 창을 닫아 메모리도 돌려준다
internal sealed class DisplayManager : IDisposable
{
    public sealed class Display
    {
        public required Screen Screen { get; init; }
        public required bool Main { get; init; }
        public required WallpaperForm Form { get; init; }
        public DesktopHost Desk { get; set; } = new();
        public bool Hidden { get; set; }
        public int AudioRate { get; set; } // 이 창의 페이지가 요청한 오디오 분석 주기
    }

    // Shown: 평소 / Hidden: 창만 숨김(페이지는 살아 있어 즉시 복귀) / Unloaded: 창을 닫음(WebView2 프로세스까지 종료)
    enum Presence { Shown, Hidden, Unloaded }
    const long UnloadAfterMs = 10 * 60_000;

    readonly bool _windowed;
    readonly ArtStore _art;
    readonly Func<string?> _mainMonitor;
    readonly System.Windows.Forms.Timer _watch = new() { Interval = 1000 };
    System.Windows.Forms.Timer? _rebuildTimer;
    List<Display> _displays = new();
    Presence _presence = Presence.Unloaded;
    long _hiddenAt, _lastCrashAt;
    bool _suspended;
    bool _recreating;
    int _lastAudioRate = -1; // 마지막으로 알린 값 (창을 다시 만들어도 어긋나지 않게)
    bool _disposed;

    // 페이지가 (재)로드되어 상태를 다시 보내야 할 때
    public event Action<Display>? PageReady;
    // 일반 창 모드(--window)에서 사용자가 창을 닫았을 때
    public event Action? WindowClosed;
    // 필요한 오디오 분석 주기가 바뀌었을 때 (앨범 모니터가 가려지거나 숨으면 0)
    public event Action<int>? AudioRateChanged;

    public IReadOnlyList<Display> Displays => _displays;
    public Display? Main => _displays.FirstOrDefault(d => d.Main);
    public bool Asleep => _presence != Presence.Shown;

    public DisplayManager(bool windowed, ArtStore art, Func<string?> mainMonitor)
    {
        _windowed = windowed;
        _art = art;
        _mainMonitor = mainMonitor;
        _watch.Tick += (_, _) => Watch();
        if (!windowed) SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    // asleep: 창을 만들지 않고 시작 (재생이 시작되면 Wake)
    public void Start(bool asleep)
    {
        if (asleep && !_windowed) Log.Write("displays: starting asleep (nothing playing)");
        else
        {
            _presence = Presence.Shown;
            Create();
        }
        _watch.Start();
    }

    public void Post(string json)
    {
        foreach (var d in _displays) d.Form.Post(json);
    }

    // 오디오는 앨범 모니터(파형·배경 반응)만 쓰므로, 그 창이 보일 때만 받는다
    public WallpaperForm? VisibleMainForm =>
        _presence == Presence.Shown && Main is { Hidden: false } m && m.Form.IsReady ? m.Form : null;

    int AudioRate => _presence == Presence.Shown && Main is { Hidden: false } m ? m.AudioRate : 0;

    public Screen MainScreen()
    {
        var screens = Screen.AllScreens;
        return screens.FirstOrDefault(s => s.DeviceName == _mainMonitor()) ?? Screen.PrimaryScreen ?? screens[0];
    }

    // 잠금·화면 꺼짐·절전 중에는 아무도 보지 않으므로 모든 창의 렌더링을 멈춘다
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            Log.Write(value ? "displays: suspended (locked / screen off / sleeping)" : "displays: resumed");
            Watch();
        }
    }

    // ───────────── 유휴 숨김 ─────────────

    // 창을 숨겨 원래 배경화면이 보이게 한다
    public void Sleep()
    {
        if (_disposed || _windowed || _presence != Presence.Shown) return;
        _presence = Presence.Hidden;
        _hiddenAt = Environment.TickCount64;
        foreach (var d in _displays)
        {
            SetHidden(d, true);
            d.Form.HideNative();
        }
        DesktopHost.RefreshWallpaper();
        NotifyAudioRate();
        Log.Write("displays: asleep → original wallpaper");
    }

    public void Wake()
    {
        if (_disposed || _presence == Presence.Shown) return;
        var was = _presence;
        _presence = Presence.Shown;
        Log.Write($"displays: wake (was {was})");
        if (was == Presence.Unloaded || _displays.Count == 0) Create();
        else
        {
            foreach (var d in _displays) d.Form.ShowNative();
            Watch(); // 가려짐 여부를 바로 다시 판단해 렌더링 재개
        }
    }

    void Unload()
    {
        _presence = Presence.Unloaded;
        CloseAll();
        DesktopHost.RefreshWallpaper();
        NotifyAudioRate();
        Log.Write("displays: unloaded (windows closed to free memory)");
    }

    // ───────────── 창 만들기 ─────────────

    public void Recreate()
    {
        if (_disposed || _presence == Presence.Unloaded) return;
        // 숨어 있는 동안엔 다시 만들 필요 없이 닫아 두면 된다 (깨어날 때 새로 만듦)
        if (_presence == Presence.Hidden) { Unload(); return; }
        CloseAll();
        Create();
    }

    // 여러 변경이 연달아 올 때 한 번만 다시 만들도록 지연
    public void RecreateSoon(int delayMs)
    {
        if (_disposed) return;
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

    void CloseAll()
    {
        _recreating = true;
        foreach (var d in _displays)
            try { if (!d.Form.IsDisposed) d.Form.Close(); } catch { }
        _recreating = false;
        _displays = new List<Display>();
    }

    void Create()
    {
        var main = MainScreen();
        var screens = _windowed ? new[] { main } : Screen.AllScreens;
        _displays = screens.Select(s => CreateDisplay(s, s.DeviceName == main.DeviceName)).ToList();
        Log.Write("displays: " + string.Join(", ", _displays.Select(d => $"{d.Screen.DeviceName} {d.Screen.Bounds} {(d.Main ? "main" : "background")}")));
        NotifyAudioRate();
    }

    Display CreateDisplay(Screen screen, bool main)
    {
        var bounds = screen.Bounds;
        var form = new WallpaperForm(_windowed, bounds, _art, backgroundOnly: !main);
        var d = new Display { Screen = screen, Main = main, Form = form };
        form.Ready += () => PageReady?.Invoke(d);
        form.AudioRateRequested += rate =>
        {
            d.AudioRate = rate;
            NotifyAudioRate();
        };
        form.Crashed += OnWebViewCrashed;
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
                Log.Error("webview init", ex);
                if (main) ShowWebViewError(ex);
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

    // 브라우저 프로세스가 죽으면 그 WebView2들은 되살릴 수 없으므로 창을 통째로 다시 만든다.
    // 계속 죽는 상황이면 1초마다 되풀이하지 않도록 간격을 벌린다.
    void OnWebViewCrashed()
    {
        long now = Environment.TickCount64;
        bool again = now - _lastCrashAt < 60_000;
        _lastCrashAt = now;
        Log.Write($"webview browser process exited → recreate windows{(again ? " (again; waiting 15s)" : "")}");
        RecreateSoon(again ? 15_000 : 1000);
    }

    // ───────────── 1초 점검 ─────────────

    void Watch()
    {
        if (_disposed) return;
        if (_presence == Presence.Hidden && Environment.TickCount64 - _hiddenAt > UnloadAfterMs)
        {
            Unload();
            return;
        }
        if (_displays.Count == 0) return;

        if (!_windowed)
        {
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
        }

        if (_presence == Presence.Shown)
        {
            var covered = _windowed ? null : CoveredMonitor();
            foreach (var d in _displays)
                SetHidden(d, _suspended || (covered is { } m && m == d.Screen.Bounds));
        }
        NotifyAudioRate();
    }

    void SetHidden(Display d, bool hidden)
    {
        if (hidden == d.Hidden) return;
        d.Hidden = hidden;
        d.Form.Post($"{{\"type\":\"visibility\",\"hidden\":{(hidden ? "true" : "false")}}}");
        d.Form.SetMemoryLow(hidden || !d.Main);
    }

    void NotifyAudioRate()
    {
        int rate = AudioRate;
        if (rate == _lastAudioRate) return;
        _lastAudioRate = rate;
        AudioRateChanged?.Invoke(rate);
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

    // 창을 다시 만들 때마다 뜨지 않도록 한 번만 알린다
    static bool _webViewErrorShown;
    const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    static void ShowWebViewError(Exception ex)
    {
        if (_webViewErrorShown) return;
        _webViewErrorShown = true;
        if (ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            var answer = MessageBox.Show(
                "화면을 그리는 데 필요한 Microsoft Edge WebView2 런타임이 이 PC에 없습니다.\n\n" +
                "다운로드 페이지를 열까요? \"Evergreen Bootstrapper\"를 받아 설치한 뒤 앱을 다시 실행해 주세요.",
                "Spotify Wallpaper", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(WebView2DownloadUrl) { UseShellExecute = true }); }
                catch (Exception e) { Log.Write("open webview2 page: " + e.Message); }
            }
            return;
        }
        MessageBox.Show("WebView2 초기화에 실패했습니다.\n" + ex.Message, "Spotify Wallpaper",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
