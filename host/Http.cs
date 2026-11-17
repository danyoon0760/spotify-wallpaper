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
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpotifyWallpaper", Program.Version));
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(personal desktop wallpaper)"));
        return c;
    }

    // 비교용 정규화: 소문자, 괄호/feat 제거, 문자·숫자만
    // ("with"는 떼지 않는다 — "Die With A Smile"처럼 제목의 일부인 경우가 많고, "(with X)"는 괄호 제거로 이미 처리됨)
    public static string Norm(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Regex.Replace(s.ToLowerInvariant(), @"[\(\[].*?[\)\]]", "");
        s = Regex.Replace(s, @"\b(feat|ft)\b.*$", "");
        s = Regex.Replace(s, @"\s-\s.*$", "");
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    // 같거나, 한쪽이 다른 쪽을 포함하면 비슷한 것으로 본다.
    // 단, 아주 짧은 이름이 긴 이름에 우연히 들어 있는 경우("Die" ⊂ "Die Young", "花" ⊂ "花火")는 제외:
    // 짧은 쪽이 4자 이상이거나 긴 쪽의 절반을 넘어야 한다.
    public static bool Similar(string a, string b)
    {
        a = Norm(a); b = Norm(b);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (!longer.Contains(shorter)) return false;
        return shorter.Length >= 4 || shorter.Length * 2 > longer.Length;
    }

    // "A, B & C" → "A"
    public static string FirstArtist(string artist) =>
        artist.Split(new[] { ", ", " & ", "、", " x " }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? artist;
}
