using System.Reflection;

namespace SpotifyWallpaper;

static class Program
{
    public static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // 실행 중인 앱에 "정상 종료해 달라"고 알리는 신호 (SpotifyWallpaper.exe --exit)
    public const string ExitSignalName = "SpotifyWallpaper.Exit";

    [STAThread]
    static void Main(string[] args)
    {
        // --exit : 이미 떠 있는 앱을 정상 종료시키고 끝낸다 (빌드 스크립트가 exe를 교체하기 전에 사용)
        if (args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase)))
        {
            if (EventWaitHandle.TryOpenExisting(ExitSignalName, out var signal))
                using (signal) signal.Set();
            return;
        }

        using var mutex = new Mutex(true, "SpotifyWallpaper.SingleInstance", out bool created);
        if (!created) return;

        // 자동 시작으로 혼자 도는 앱이라, 죽더라도 이유는 로그에 남긴다
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("ui thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write($"FATAL unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("unobserved task", e.Exception);
            e.SetObserved();
        };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // --window : 바탕화면에 붙이지 않고 일반 창으로 실행 (디버깅용)
        bool windowed = args.Any(a => a.Equals("--window", StringComparison.OrdinalIgnoreCase));
        Log.Write($"──── start v{Version}{(windowed ? " (windowed)" : "")} · {Environment.OSVersion.VersionString} · .NET {Environment.Version}");
        try
        {
            Application.Run(new WallpaperApp(windowed));
            Log.Write("exit");
        }
        catch (Exception ex)
        {
            Log.Error("startup", ex);
            throw;
        }
    }
}
