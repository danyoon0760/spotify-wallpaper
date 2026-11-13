using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace SpotifyWallpaper;

// 공용 HttpClient와 곡 제목/아티스트 비교 도우미
internal static class Http
{
    public static readonly HttpClient Client = Create();

    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpotifyWallpaper", "0.1"));
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(personal desktop wallpaper)"));
        return c;
    }

    // 비교용 정규화: 소문자, 괄호/feat 제거, 문자·숫자만
    public static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Regex.Replace(s.ToLowerInvariant(), @"[\(\[].*?[\)\]]", "");
        s = Regex.Replace(s, @"\b(feat|ft|with)\b.*$", "");
        s = Regex.Replace(s, @"\s-\s.*$", "");
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    public static bool Similar(string a, string b)
    {
        a = Norm(a); b = Norm(b);
        if (a.Length == 0 || b.Length == 0) return false;
        return a == b || a.Contains(b) || b.Contains(a);
    }

    // "A, B & C" → "A"
    public static string FirstArtist(string artist) =>
        artist.Split(new[] { ", ", " & ", "、", " x " }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? artist;
}
