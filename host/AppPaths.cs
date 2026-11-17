namespace SpotifyWallpaper;

// 앱이 쓰는 폴더 모음
//  - Data  (%APPDATA%):      설정, 로그 — 사용자가 열어 볼 만한 것
//  - Local (%LOCALAPPDATA%): 캐시 — 지워도 다시 만들어지는 것
internal static class AppPaths
{
    const string AppName = "SpotifyWallpaper";

    public static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static readonly string Local = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    public static readonly string WebView2 = Path.Combine(Local, "WebView2");
    public static readonly string LyricsCache = Path.Combine(Local, "lyrics-cache");
    public static readonly string ArtCache = Path.Combine(Local, "art-cache");
}
