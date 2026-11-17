using Velopack;
using Velopack.Sources;

namespace SpotifyWallpaper;

// GitHub Releases에서 새 버전을 확인·내려받는다 (Velopack)
//  - 설치본에서만 동작 (개발 중 빌드나 dist 폴더 실행은 IsAvailable = false)
//  - 켜고 1분 뒤, 이후 6시간마다 확인 → 새 버전이 있으면 미리 내려받아 두고 트레이에서 알린다
//  - 내려받은 업데이트는 트레이 메뉴로 바로 설치하거나, 다음에 앱이 켜질 때 자동으로 적용된다
internal sealed class UpdateService : IDisposable
{
    public const string RepoUrl = "https://github.com/danyoon0760/spotify-wallpaper";
    const int FirstCheckDelayMs = 60_000;
    const int CheckIntervalMs = 6 * 60 * 60_000;

    readonly UpdateManager? _mgr;
    readonly System.Windows.Forms.Timer _timer = new();
    readonly SynchronizationContext _ui;
    VelopackAsset? _ready;
    bool _disposed;

    // 상태(확인 중 / 준비됨)가 바뀌었을 때
    public event Action? StateChanged;
    // 사용자에게 알릴 말 (제목, 내용)
    public event Action<string, string>? Notice;

    public bool IsAvailable => _mgr != null;
    public bool Checking { get; private set; }
    public string? ReadyVersion => _ready?.Version.ToString();

    public UpdateService(SynchronizationContext ui)
    {
        _ui = ui;
        try
        {
            var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (mgr.IsInstalled)
            {
                _mgr = mgr;
                _ready = mgr.UpdatePendingRestart; // 지난번에 내려받아 두고 아직 적용 안 한 것
            }
        }
        catch (Exception ex) { Log.Write("update init: " + ex.Message); }
        if (_mgr == null) return;

        _timer.Interval = FirstCheckDelayMs;
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = CheckIntervalMs;
            _ = CheckAsync(manual: false);
        };
        _timer.Start();
    }

    public async Task CheckAsync(bool manual)
    {
        if (_mgr == null || _disposed || Checking) return;
        if (_ready != null)
        {
            if (manual) Notice?.Invoke("업데이트 준비됨", $"v{ReadyVersion}을(를) 설치할 준비가 됐습니다. 트레이 메뉴에서 설치하세요.");
            return;
        }
        SetChecking(true);
        try
        {
            var info = await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info == null)
            {
                Log.Write("update: up to date");
                if (manual) Post(() => Notice?.Invoke("최신 버전", $"v{Program.Version}이(가) 최신 버전입니다."));
                return;
            }
            Log.Write($"update: downloading v{info.TargetFullRelease.Version}");
            await _mgr.DownloadUpdatesAsync(info).ConfigureAwait(false);
            _ready = info.TargetFullRelease;
            Log.Write($"update: v{ReadyVersion} ready");
            Post(() => Notice?.Invoke("새 버전 준비됨",
                $"v{ReadyVersion}을(를) 내려받았습니다. 트레이 메뉴에서 설치하거나, 다음에 켤 때 자동으로 적용됩니다."));
        }
        catch (Exception ex)
        {
            Log.Write("update check: " + ex.Message);
            if (manual) Post(() => Notice?.Invoke("업데이트 확인 실패", "인터넷 연결을 확인한 뒤 다시 시도해 주세요."));
        }
        finally { SetChecking(false); }
    }

    // 앱이 끝나기를 기다렸다가 업데이트를 적용하고 다시 켜는 프로세스를 띄운다.
    // 호출한 쪽은 곧바로 앱을 정상 종료해야 한다. 실패하면 false.
    public bool ApplyAndRestart()
    {
        if (_mgr == null || _ready == null) return false;
        try
        {
            Log.Write($"update: applying v{ReadyVersion} and restarting");
            _mgr.WaitExitThenApplyUpdates(_ready, silent: true, restart: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("update apply: " + ex.Message);
            Notice?.Invoke("업데이트 설치 실패", ex.Message);
            return false;
        }
    }

    void SetChecking(bool value) => Post(() =>
    {
        Checking = value;
        StateChanged?.Invoke();
    });

    void Post(Action a) => _ui.Post(_ => { if (!_disposed) a(); }, null);

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
