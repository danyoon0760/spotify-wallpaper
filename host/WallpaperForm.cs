using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SpotifyWallpaper;

// WebView2로 web/index.html을 띄우는 창. 바탕화면에 붙거나(--window 없이) 일반 창으로 뜬다.
public sealed class WallpaperForm : Form
{
    const string HostName = "wallpaper.example";

    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    readonly bool _windowed;
    readonly bool _backgroundOnly;

    public bool IsReady { get; private set; }
    public event Action? Ready;

    // backgroundOnly: 보조 모니터용 — 앨범·글자 없이 흐린 배경만 그림
    public WallpaperForm(bool windowed, Rectangle screen, bool backgroundOnly = false)
    {
        _windowed = windowed;
        _backgroundOnly = backgroundOnly;
        Text = "Spotify Wallpaper";
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
        if (windowed)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = new Rectangle(screen.X + 80, screen.Y + 80, 1280, 720);
        }
        else
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            Bounds = screen;
        }
        Controls.Add(_web);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (!_windowed) cp.ExStyle |= (int)Native.WS_EX_TOOLWINDOW; // Alt+Tab에 안 보이게
            return cp;
        }
    }

    public async Task InitAsync()
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyWallpaper", "WebView2");
        string args =
            "--disable-features=CalculateNativeWinOcclusion " +
            "--disable-background-timer-throttling " +
            "--disable-renderer-backgrounding " +
            "--disable-backgrounding-occluded-windows " +
            "--autoplay-policy=no-user-gesture-required";
        // 개발용: WALLPAPER_DEBUG_PORT=9229 로 실행하면 크롬 원격 디버깅 포트를 연다
        if (Environment.GetEnvironmentVariable("WALLPAPER_DEBUG_PORT") is { Length: > 0 } port)
            args += $" --remote-debugging-port={port}";
        var options = new CoreWebView2EnvironmentOptions(args);
        var env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
        await _web.EnsureCoreWebView2Async(env);

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.SetVirtualHostNameToFolderMapping(HostName, WebRoot(), CoreWebView2HostResourceAccessKind.Allow);

        core.NavigationStarting += (_, _) => IsReady = false;
        core.WebMessageReceived += (_, e) =>
        {
            string json;
            try { json = e.WebMessageAsJson; } catch { return; }
            if (json.Contains("\"ready\""))
            {
                IsReady = true;
                Ready?.Invoke();
            }
        };
        core.ProcessFailed += (_, e) =>
        {
            Log.Write("webview process failed: " + e.ProcessFailedKind);
            BeginInvoke(() => { try { core.Reload(); } catch { } });
        };
        core.Navigate($"https://{HostName}/index.html{(_backgroundOnly ? "?mode=background" : "")}");
        SetMemoryLow(_backgroundOnly);
    }

    // 개발 중에는 소스의 web 폴더를 직접 쓰고(새로고침만으로 반영), 배포본은 exe 옆 web 폴더
    static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "web", "index.html")) &&
                Directory.Exists(Path.Combine(d.FullName, "host")))
                return Path.Combine(d.FullName, "web");
        }
        return Path.Combine(AppContext.BaseDirectory, "web");
    }

    public void Post(string json)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => Post(json)); } catch { }
            return;
        }
        if (!IsReady) return;
        try { _web.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
    }

    public void OpenDevTools() => _web.CoreWebView2?.OpenDevToolsWindow();
    public void Reload() => _web.CoreWebView2?.Reload();

    // 배경 전용이거나 가려진 창은 WebView2에 메모리를 적게 쓰라고 알림 (캐시 등을 덜 붙잡음)
    public void SetMemoryLow(bool low)
    {
        try
        {
            if (_web.CoreWebView2 is { } core)
                core.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch { }
    }
}
