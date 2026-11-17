using System.Globalization;
using System.Text.RegularExpressions;

namespace SpotifyWallpaper;

// 검색으로 얻은 가사 후보 하나
//  Rank: 검색 결과 순위 (NetEase는 제목 표기가 달라도 상위 결과 + 길이 일치면 인정)
internal sealed record LyricCandidate(
    string Source, string TrackName, string ArtistName, double Duration, string Synced, bool Instrumental, int Rank);

// 찾으려는 곡 (Duration: 초, 모르면 0)
internal sealed record LyricQuery(string Title, string Clean, string Artist, string Album, double Duration, bool SpeedVariant)
{
    public static LyricQuery From(TrackInfo track, double durationMs)
    {
        string title = track.Title.Trim();
        return new LyricQuery(
            title,
            LyricsMatcher.CleanTitle(title),
            Http.FirstArtist(track.Artist),
            track.Album,
            durationMs / 1000,
            LyricsMatcher.IsSpeedVariant(title) || LyricsMatcher.IsSpeedVariant(track.Album));
    }
}

// 가사 후보 고르기와 LRC 해석 — 네트워크와 무관한 순수 함수만 모아 테스트하기 쉽게 한다.
//  - 곡 길이가 ±3초 안에 맞는 "싱크 가사"만 사용 (Sped up / Slowed 버전은 길이 비율로 보정)
//  - 맞는 게 없으면 고르지 않는다 (어긋난 가사를 억지로 띄우지 않음)
internal static class LyricsMatcher
{
    public const double DurationTolerance = 3.0;

    static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
    static readonly Regex SpeedVariant = new(@"sped\s*up|speed\s*up|slowed|nightcore|reverb|daycore|배속", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // NetEase 가사 앞에 붙는 작사/작곡 등 크레딧 줄
    static readonly Regex CreditLine = new(
        @"^\s*(作词|作詞|作曲|编曲|編曲|词|詞|曲|制作人|製作人|监制|出品|发行|混音|录音|和声|母带|吉他|贝斯|鼓|键盘|弦乐|演唱|原唱|翻唱|翻译|OP|SP|ISRC|Lyrics|Lyricist|Composer|Arranger|Producer|Written by|Composed by)\s*[:：]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSpeedVariant(string text) => SpeedVariant.IsMatch(text);
    public static bool HasTimeTags(string lrc) => TimeTag.IsMatch(lrc);

    // "(feat. X)", "- Remastered 2011", "(Sped Up)", "[Live]" 등 제거
    // (괄호 없는 "with"는 떼지 않는다 — "Die With A Smile"이 "Die"가 되어 검색어를 망친다)
    public static string CleanTitle(string t)
    {
        string s = Regex.Replace(t, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        s = Regex.Replace(s, @"\s+-\s+.*$", "");
        s = Regex.Replace(s, @"\s+(feat\.?|ft\.?)\s+.*$", "", RegexOptions.IgnoreCase);
        s = s.Trim();
        return s.Length > 0 ? s : t;
    }

    static bool TitleOk(LyricCandidate c, LyricQuery q) => Http.Similar(c.TrackName, q.Clean) || Http.Similar(c.TrackName, q.Title);
    static bool ArtistOk(LyricCandidate c, LyricQuery q) => Http.Similar(c.ArtistName, q.Artist);
    static double Diff(LyricCandidate c, LyricQuery q) =>
        q.Duration > 0 && c.Duration > 0 ? Math.Abs(c.Duration - q.Duration) : double.MaxValue;
    static bool Usable(LyricCandidate c) => c.Synced.Length > 0 || c.Instrumental;

    // 가장 알맞은 후보와 시간 배율(배속 버전이면 1이 아님). 없으면 null.
    // exactOnly: 길이가 맞는 후보(1번 규칙)만 인정 — 후보를 아직 다 모으지 못한 중간 단계에서 사용
    public static (LyricCandidate best, double scale)? Pick(IEnumerable<LyricCandidate> candidates, LyricQuery q, bool exactOnly = false)
    {
        var usable = candidates.Where(Usable).ToList();

        // 1) 길이가 맞는 것: 제목·아티스트 중 하나 이상 일치, 또는 NetEase 상위 결과(표기만 다른 경우)
        var exact = usable
            .Where(c => Diff(c, q) <= DurationTolerance)
            .Where(c => (TitleOk(c, q) && (ArtistOk(c, q) || Diff(c, q) <= 2)) ||
                        (ArtistOk(c, q) && Diff(c, q) <= 1.5) ||
                        (c.Source == "netease" && c.Rank < 3 && Diff(c, q) <= 1.5))
            .OrderBy(c => Diff(c, q))
            .ThenBy(c => c.Source == "lrclib" ? 0 : 1)
            .FirstOrDefault();
        if (exact != null) return (exact, 1.0);
        if (exactOnly) return null;

        // 2) 배속 버전: 원곡 가사를 길이 비율로 보정 (제목·아티스트 모두 일치해야 함)
        if (q.SpeedVariant && q.Duration > 0)
        {
            var original = usable
                .Where(c => c.Synced.Length > 0 && c.Duration > 0 && TitleOk(c, q) && ArtistOk(c, q))
                .Where(c => q.Duration / c.Duration is > 0.55 and < 1.6)
                .OrderBy(c => c.Source == "lrclib" ? 0 : 1)
                .FirstOrDefault();
            if (original != null) return (original, q.Duration / original.Duration);
        }

        // 3) 곡 길이를 모를 때만: 제목+아티스트 일치하는 첫 싱크 가사
        if (q.Duration <= 0)
        {
            var any = usable.FirstOrDefault(c => TitleOk(c, q) && ArtistOk(c, q));
            if (any != null) return (any, 1.0);
        }
        return null;
    }

    // 고르지 못했을 때 로그에 남길 "가장 가까웠던 후보" 설명 (매칭 기준을 손볼 때의 근거 자료)
    public static string Describe(LyricCandidate c, LyricQuery q)
    {
        double diff = Diff(c, q);
        string dur = diff == double.MaxValue ? "Δ?" : $"Δ{c.Duration - q.Duration:+0.0;-0.0;0.0}s";
        string kind = c.Instrumental ? "instrumental" : c.Synced.Length > 0 ? "synced" : "plain-only";
        return $"[{c.Source}#{c.Rank}] '{c.TrackName}' ({c.ArtistName}) {dur} title={(TitleOk(c, q) ? "ok" : "no")} artist={(ArtistOk(c, q) ? "ok" : "no")} {kind}";
    }

    // 탈락한 후보 중 가까운 순서로 (제목·아티스트가 맞는 것 먼저, 그다음 길이 차이)
    public static IEnumerable<LyricCandidate> Nearest(IEnumerable<LyricCandidate> candidates, LyricQuery q, int count) =>
        candidates
            .OrderByDescending(c => (TitleOk(c, q) ? 2 : 0) + (ArtistOk(c, q) ? 1 : 0))
            .ThenBy(c => Diff(c, q))
            .Take(count);

    // LRC → 시간순 줄 목록. scale: 배속 버전 보정 배율
    public static List<LyricLine> ParseLrc(string lrc, double scale = 1.0)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var matches = TimeTag.Matches(raw);
            if (matches.Count == 0) continue;
            string text = raw[(matches[^1].Index + matches[^1].Length)..].Trim();
            if (CreditLine.IsMatch(text)) continue;
            foreach (Match m in matches)
            {
                double min = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(Math.Round((min * 60 + sec) * 1000 * scale), text));
            }
        }
        // 같은 시각의 줄은 원래 순서 유지 (List.Sort는 순서를 보장하지 않음)
        return lines.OrderBy(l => l.T).ToList();
    }
}
