namespace SpotifyWallpaper;

// %APPDATA%\SpotifyWallpaper\log.txt
// 512KB를 넘으면 log.old.txt로 넘기고 새로 시작한다 (직전 기록 한 벌은 항상 남음).
internal static class Log
{
    const long MaxBytes = 512_000;
    static readonly string FilePath = Path.Combine(AppPaths.Data, "log.txt");
    static readonly string OldPath = Path.Combine(AppPaths.Data, "log.old.txt");
    static readonly object Gate = new();

    // 테스트에서는 꺼서 실제 로그 파일을 건드리지 않게 한다
    internal static bool Enabled = true;

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Data);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, OldPath, overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { /* 로그 실패로 앱이 죽으면 안 됨 */ }
    }

    public static void Error(string where, Exception ex) => Write($"ERROR {where}: {ex}");
}
