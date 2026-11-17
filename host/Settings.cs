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
    // 가사 표시를 켜고 끄는 전역 단축키 (예: "Ctrl+Alt+L"). 빈 문자열이면 사용 안 함
    public string Hotkey { get; set; } = "Ctrl+Alt+L";
    // 가사 싱크 보정(ms). 양수 = 가사가 더 빨리 나옴 (블루투스 지연 등 소리가 늦게 들릴 땐 음수)
    public int LyricsOffsetMs { get; set; } = 0;
    // Spotify가 꺼져 있을 땐 창을 숨겨 원래 배경화면을 보여 줌
    public bool HideWhenIdle { get; set; } = true;

    static readonly string FilePath = Path.Combine(AppPaths.Data, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) { Log.Write("settings load failed, using defaults: " + ex.Message); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            // 쓰는 도중 꺼져도 설정 파일이 깨지지 않게 임시 파일에 쓴 뒤 바꿔치기
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
    }

    // 렌더러에 보내는 설정 메시지
    public object ToMessage() => new { type = "settings", lyrics = Lyrics, fps = Fps, lyricsOffset = LyricsOffsetMs };
}
