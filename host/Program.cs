namespace SpotifyWallpaper;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "SpotifyWallpaper.SingleInstance", out bool created);
        if (!created) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // --window : 바탕화면에 붙이지 않고 일반 창으로 실행 (디버깅용)
        bool windowed = args.Any(a => a.Equals("--window", StringComparison.OrdinalIgnoreCase));
        Application.Run(new WallpaperApp(windowed));
    }
}
