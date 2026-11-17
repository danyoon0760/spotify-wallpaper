using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SpotifyWallpaper;

// WebView2로 web/index.html을 띄우는 창. 바탕화면에 붙거나(--window 없이) 일반 창으로 뜬다.
internal sealed class WallpaperForm : Form
{
    const string HostName = "wallpaper.example";

    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    readonly ArtStore _art;
    readonly bool _windowed;
    readonly bool _backgroundOnly;

    public bool IsReady { get; private set; }
    // 페이지가 (재)로드되어 메시지를 받을 준비가 됐을 때
    public event Action? Ready;
    // 페이지가 원하는 오디오 분석 주기(Hz, 0 = 필요 없음)
    public event Action<int>? AudioRateRequested;
    // WebView2 브라우저 프로세스가 죽었을 때 — 새로고침으로는 못 살리고 창을 다시 만들어야 한다
    public event Action? Crashed;

    // backgroundOnly: 보조 모니터용 — 앨범·글자 없이 흐린 배경만 그림
    public WallpaperForm(bool windowed, Rectangle screen, ArtStore art, bool backgroundOnly = false)
    {
        _windowed = windowed;
        _art = art;
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
        var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView2, options);
        await _web.EnsureCoreWebView2Async(env);

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.SetVirtualHostNameToFolderMapping(HostName, WebRoot(), CoreWebView2HostResourceAccessKind.Allow);

        // 앨범아트는 디스크 저장소에서 직접 꺼내 준다 (https://art.wallpaper.example/<파일명>)
        core.AddWebResourceRequestedFilter($"https://{ArtStore.Host}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) => ServeArt(env, e);

        core.NavigationStarting += (_, _) =>
        {
            IsReady = false;
            AudioRateRequested?.Invoke(0); // 새 페이지가 다시 요청할 때까지는 필요 없음
        };
        core.WebMessageReceived += (_, e) => OnWebMessage(e);
        core.ProcessFailed += (_, e) => OnProcessFailed(core, e);
        core.Navigate($"https://{HostName}/index.html{(_backgroundOnly ? "?mode=background" : "")}");
        SetMemoryLow(_backgroundOnly);
    }

    void ServeArt(CoreWebView2Environment env, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            if (!uri.Host.Equals(ArtStore.Host, StringComparison.OrdinalIgnoreCase)) return;
            // 페이지(다른 출처)가 WebGL 텍스처로 올릴 수 있도록 CORS 허용 헤더를 붙인다
            const string cors = "Access-Control-Allow-Origin: *";
            e.Response = _art.Read(uri.AbsolutePath.TrimStart('/')) is { } file
                ? env.CreateWebResourceResponse(new MemoryStream(file.Bytes), 200, "OK", $"Content-Type: {file.Mime}\n{cors}\nCache-Control: no-store")
                : env.CreateWebResourceResponse(null, 404, "Not Found", cors);
        }
        catch (Exception ex) { Log.Write("art request: " + ex.Message); }
    }

    // 웹 → 호스트: { type: 'ready' } / { type: 'audio', rate }
    void OnWebMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) return;
            switch (type.GetString())
            {
                case "ready":
                    IsReady = true;
                    Ready?.Invoke();
                    break;
                case "audio":
                    if (root.TryGetProperty("rate", out var r) && r.TryGetInt32(out int rate))
                        AudioRateRequested?.Invoke(Math.Clamp(rate, 0, 60));
                    break;
            }
        }
        catch (Exception ex) { Log.Write("web message: " + ex.Message); }
    }

    void OnProcessFailed(CoreWebView2 core, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Write($"webview process failed: {e.ProcessFailedKind} ({e.Reason})");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // 브라우저 프로세스가 사라지면 이 WebView2는 더 쓸 수 없다 → 창을 통째로 다시 만들게 알림
                IsReady = false;
                SafeBeginInvoke(() => Crashed?.Invoke());
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                SafeBeginInvoke(() => { try { core.Reload(); } catch (Exception ex) { Log.Write("reload after render failure: " + ex.Message); } });
                break;
            // GPU·유틸리티 프로세스는 WebView2가 스스로 다시 띄운다
        }
    }

    void SafeBeginInvoke(Action action)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(action); } catch { }
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

    // 창만 감추거나 다시 보임 (페이지는 살려 둠). WinForms의 Visible을 건드리면 바탕화면에 붙인 상태가 흐트러질 수 있어 직접 호출
    public void HideNative() { if (IsHandleCreated && !IsDisposed) Native.ShowWindow(Handle, Native.SW_HIDE); }
    public void ShowNative() { if (IsHandleCreated && !IsDisposed) Native.ShowWindow(Handle, Native.SW_SHOWNA); }

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
