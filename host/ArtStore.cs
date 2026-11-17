using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SpotifyWallpaper;

// 앨범아트 디스크 저장소 (%LOCALAPPDATA%\SpotifyWallpaper\art-cache)
//  - 이미지는 메모리에 쥐고 있지 않고 파일로 두며, 화면에는 주소만 보낸다 (https://art.wallpaper.example/<파일명>)
//  - hi-<곡 id>.<ext>  고해상도 아트. 한 번 찾은 곡은 다시 검색하지 않음
//    hi-<곡 id>.none   "찾아봤지만 없음" 표시 (7일 뒤 다시 검색)
//    th-<해시>.<ext>   Spotify가 준 썸네일
//  - 오래 안 쓴 파일부터 지워 개수를 제한한다
internal sealed class ArtStore
{
    public const string Host = "art.wallpaper.example";
    public const string HiRes = "hi", Thumb = "th";

    const int MaxHiResFiles = 400;   // 장당 200~400KB → 대략 100MB 안쪽
    const int MaxThumbFiles = 40;
    const int PruneEverySaves = 25;
    static readonly TimeSpan MissingTtl = TimeSpan.FromDays(7);
    static readonly string[] Extensions = { ".jpg", ".png", ".webp" };
    static readonly Regex ValidName = new(@"^(hi|th)-[0-9a-f]{40}\.(jpg|png|webp)$", RegexOptions.Compiled);

    readonly string _dir;
    int _savesSincePrune;

    public ArtStore(string? dir = null) => _dir = dir ?? AppPaths.ArtCache;

    public static string Url(string fileName) => $"https://{Host}/{fileName}";

    // 곡 키나 썸네일 해시처럼 임의의 문자열을 파일 이름에 쓸 수 있는 id로
    public static string Id(string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // 저장된 파일 이름 (없으면 null). 찾으면 "최근에 씀"으로 표시해 정리 대상에서 뒤로 미룬다
    public string? Find(string kind, string id)
    {
        try
        {
            foreach (var ext in Extensions)
            {
                string name = $"{kind}-{id}{ext}";
                string path = Path.Combine(_dir, name);
                if (!File.Exists(path)) continue;
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return name;
            }
        }
        catch (Exception ex) { Log.Write("art cache find: " + ex.Message); }
        return null;
    }

    // 이미지가 아니면 null
    public string? Save(string kind, string id, byte[] bytes)
    {
        string? ext = ExtensionOf(bytes);
        if (ext == null) return null;
        try
        {
            Directory.CreateDirectory(_dir);
            string name = $"{kind}-{id}{ext}";
            string path = Path.Combine(_dir, name);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            if (Interlocked.Increment(ref _savesSincePrune) >= PruneEverySaves)
            {
                Interlocked.Exchange(ref _savesSincePrune, 0);
                _ = Task.Run(Prune);
            }
            return name;
        }
        catch (Exception ex)
        {
            Log.Write("art cache save: " + ex.Message);
            return null;
        }
    }

    public bool IsKnownMissing(string kind, string id)
    {
        try
        {
            var marker = new FileInfo(Path.Combine(_dir, $"{kind}-{id}.none"));
            return marker.Exists && DateTime.UtcNow - marker.LastWriteTimeUtc < MissingTtl;
        }
        catch { return false; }
    }

    public void MarkMissing(string kind, string id)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllBytes(Path.Combine(_dir, $"{kind}-{id}.none"), Array.Empty<byte>());
        }
        catch (Exception ex) { Log.Write("art cache mark: " + ex.Message); }
    }

    // 화면(WebView2)의 요청에 응답할 때 사용. 저장소가 만든 이름 형식만 받아 다른 경로는 읽지 못하게 한다
    public (byte[] Bytes, string Mime)? Read(string fileName)
    {
        if (!ValidName.IsMatch(fileName)) return null;
        try
        {
            string path = Path.Combine(_dir, fileName);
            if (!File.Exists(path)) return null;
            return (File.ReadAllBytes(path), MimeOf(Path.GetExtension(fileName)));
        }
        catch (Exception ex)
        {
            Log.Write("art cache read: " + ex.Message);
            return null;
        }
    }

    // 오래 안 쓴 파일부터 지운다 (방금 쓴 파일은 화면이 아직 쓰고 있을 수 있으므로 1시간은 건드리지 않음)
    public void Prune()
    {
        try
        {
            var dir = new DirectoryInfo(_dir);
            if (!dir.Exists) return;
            var now = DateTime.UtcNow;
            var files = dir.GetFiles();
            int removed = 0;

            void Trim(string prefix, int keep)
            {
                var old = files
                    .Where(f => f.Name.StartsWith(prefix, StringComparison.Ordinal) && Extensions.Contains(f.Extension))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Skip(keep)
                    .Where(f => now - f.LastWriteTimeUtc > TimeSpan.FromHours(1));
                foreach (var f in old) { f.Delete(); removed++; }
            }
            Trim(HiRes + "-", MaxHiResFiles);
            Trim(Thumb + "-", MaxThumbFiles);

            foreach (var f in files)
            {
                bool staleMarker = f.Extension == ".none" && now - f.LastWriteTimeUtc > MissingTtl;
                bool leftoverTmp = f.Extension == ".tmp" && now - f.LastWriteTimeUtc > TimeSpan.FromHours(1);
                if (staleMarker || leftoverTmp) { f.Delete(); removed++; }
            }
            if (removed > 0) Log.Write($"art cache: pruned {removed} file(s)");
        }
        catch (Exception ex) { Log.Write("art cache prune: " + ex.Message); }
    }

    // 파일 앞부분으로 형식 판별
    internal static string? ExtensionOf(byte[] b)
    {
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' &&
            b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return ".webp";
        return null;
    }

    static string MimeOf(string ext) => ext switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };
}
