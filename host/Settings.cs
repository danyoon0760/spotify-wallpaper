using System.Text.Json;

namespace SpotifyWallpaper;

// %APPDATA%\SpotifyWallpaper\settings.json
public sealed class Settings
{
    public bool Lyrics { get; set; } = false;
    public bool HiResArtwork { get; set; } = true;
    public int Fps { get; set; } = 60;
    // 앨범·가사를 띄울 모니터 (Screen.DeviceName, 예: \\.\DISPLAY2). 없으면 주 모니터
    public string? MainMonitor { get; set; }

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyWallpaper");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* 손상된 설정은 무시하고 기본값 */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    // 렌더러에 보내는 설정 메시지
    public object ToMessage() => new { type = "settings", lyrics = Lyrics, fps = Fps };
}
