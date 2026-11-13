using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpotifyWallpaper;

public sealed record LyricLine(double T, string Text);
public sealed record LyricsResult(string Status, IReadOnlyList<LyricLine> Lines);

// 서버 오류 등으로 "가사가 있는지 없는지" 판단하지 못했을 때. (가사 없음과 구분)
public sealed class LyricsUnavailableException(string message) : Exception(message);

// 싱크 가사 검색: LRCLIB + NetEase 를 동시에 조회
//  - 곡 길이가 ±3초 안에 맞는 "싱크 가사"만 사용 (Sped up / Slowed 버전은 길이 비율로 보정)
//  - 맞는 게 없으면 "가사 없음" (어긋난 가사를 억지로 띄우지 않음)
//  - 503 같은 일시 오류는 실패한 요청만 짧게 재시도, 판단 불가면 LyricsUnavailableException
//  - 결과는 디스크에 저장 ("가사 없음"은 하루 뒤 다시 확인)
public sealed class LyricsService
{
    const int CacheVersion = 3;
    const double DurationTolerance = 3.0;
    static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(1); // 검색 결과가 가끔 흔들려서 "없음"은 짧게만 기억
    static readonly int[] RequestRetryDelays = { 500, 1000, 2000 };
    static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
    static readonly Regex SpeedVariant = new(@"sped\s*up|speed\s*up|slowed|nightcore|reverb|daycore|배속", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // NetEase 가사 앞에 붙는 작사/작곡 등 크레딧 줄
    static readonly Regex CreditLine = new(
        @"^\s*(作词|作詞|作曲|编曲|編曲|词|詞|曲|制作人|製作人|监制|出品|发行|混音|录音|和声|母带|吉他|贝斯|鼓|键盘|弦乐|演唱|原唱|翻唱|翻译|OP|SP|ISRC|Lyrics|Lyricist|Composer|Arranger|Producer|Written by|Composed by)\s*[:：]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly LyricsResult None = new("none", Array.Empty<LyricLine>());

    readonly Dictionary<string, LyricsResult> _memory = new();
    readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpotifyWallpaper", "lyrics-cache");

    // Rank: 검색 결과 순위 (NetEase는 제목 표기가 달라도 상위 결과 + 길이 일치면 인정)
    sealed record Candidate(string Source, string TrackName, string ArtistName, double Duration, string Synced, bool Instrumental, int Rank);

    // 판단할 수 없으면 LyricsUnavailableException
    public async Task<LyricsResult> GetAsync(TrackInfo track, double durationMs, CancellationToken ct)
    {
        if (TryCache(track.Key, out var cached)) return cached;
        var result = await FetchAsync(track, durationMs, ct);
        Store(track.Key, result);
        return result;
    }

    async Task<LyricsResult> FetchAsync(TrackInfo track, double durationMs, CancellationToken ct)
    {
        string artist = Http.FirstArtist(track.Artist);
        string title = track.Title.Trim();
        string clean = CleanTitle(title);
        double dur = durationMs / 1000;
        bool speedVariant = SpeedVariant.IsMatch(title) || SpeedVariant.IsMatch(track.Album);
        int failures = 0;

        // 실패한 요청은 null로 돌려주고 개수만 센다 (나머지 요청 결과는 살림)
        async Task<List<Candidate>?> Try(string name, Func<Task<List<Candidate>>> f)
        {
            try { return await f(); }
            catch (LyricsUnavailableException ex)
            {
                Interlocked.Increment(ref failures);
                Log.Write($"lyrics {name} failed: {ex.Message}");
                return null;
            }
        }

        var tasks = new List<Task<List<Candidate>?>>
        {
            Try("netease", () => NetEaseAsync($"{clean} {artist}", ct)),
            Try("lrclib-search", () => LrclibSearchAsync($"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}", ct)),
        };
        if (dur > 0 && !string.IsNullOrWhiteSpace(track.Album))
            tasks.Add(Try("lrclib-get", () => LrclibGetAsync(title, artist, track.Album, dur, ct)));
        if (!clean.Equals(title, StringComparison.OrdinalIgnoreCase))
            tasks.Add(Try("lrclib-clean", () => LrclibSearchAsync($"track_name={Uri.EscapeDataString(clean)}&artist_name={Uri.EscapeDataString(artist)}", ct)));
        await Task.WhenAll(tasks);

        var candidates = tasks.SelectMany(t => t.Result ?? new List<Candidate>()).ToList();
        var pick = Pick(candidates, title, clean, artist, dur, speedVariant);

        // 못 찾았으면 LRCLIB 넓은 검색 한 번 더
        if (pick == null && failures == 0)
        {
            var wide = await Try("lrclib-wide", () => LrclibSearchAsync($"q={Uri.EscapeDataString(clean + " " + artist)}", ct));
            if (wide != null) candidates.AddRange(wide);
            pick = Pick(candidates, title, clean, artist, dur, speedVariant);
        }

        if (pick == null)
        {
            if (failures > 0) throw new LyricsUnavailableException($"{failures} request(s) failed, no match");
            Log.Write($"lyrics '{title}': no synced lyrics within ±{DurationTolerance}s ({candidates.Count} candidates)");
            return None; // 모든 요청이 성공했는데 맞는 싱크 가사 없음 → 가사 없음
        }

        var (best, scale) = pick.Value;
        Log.Write($"lyrics '{title}' → [{best.Source}] '{best.TrackName}' ({best.ArtistName}) dur {dur:F1}s vs {best.Duration:F1}s scale {scale:F3}");
        if (best.Instrumental) return new("instrumental", Array.Empty<LyricLine>());
        var lines = ParseLrc(best.Synced, scale);
        return lines.Count(l => l.Text.Length > 0) >= 2 ? new("found", lines) : None;
    }

    static (Candidate best, double scale)? Pick(
        List<Candidate> candidates, string title, string clean, string artist, double dur, bool speedVariant)
    {
        bool TitleOk(Candidate c) => Http.Similar(c.TrackName, clean) || Http.Similar(c.TrackName, title);
        bool ArtistOk(Candidate c) => Http.Similar(c.ArtistName, artist);
        double Diff(Candidate c) => dur > 0 && c.Duration > 0 ? Math.Abs(c.Duration - dur) : double.MaxValue;

        var usable = candidates.Where(c => c.Synced.Length > 0 || c.Instrumental).ToList();

        // 1) 길이가 맞는 것: 제목·아티스트 중 하나 이상 일치, 또는 NetEase 상위 결과(표기만 다른 경우)
        var exact = usable
            .Where(c => Diff(c) <= DurationTolerance)
            .Where(c => (TitleOk(c) && (ArtistOk(c) || Diff(c) <= 2)) ||
                        (ArtistOk(c) && Diff(c) <= 1.5) ||
                        (c.Source == "netease" && c.Rank < 3 && Diff(c) <= 1.5))
            .OrderBy(Diff)
            .ThenBy(c => c.Source == "lrclib" ? 0 : 1)
            .FirstOrDefault();
        if (exact != null) return (exact, 1.0);

        // 2) 배속 버전: 원곡 가사를 길이 비율로 보정 (제목·아티스트 모두 일치해야 함)
        if (speedVariant && dur > 0)
        {
            var original = usable
                .Where(c => c.Synced.Length > 0 && c.Duration > 0 && TitleOk(c) && ArtistOk(c))
                .Where(c => dur / c.Duration is > 0.55 and < 1.6)
                .OrderBy(c => c.Source == "lrclib" ? 0 : 1)
                .FirstOrDefault();
            if (original != null) return (original, dur / original.Duration);
        }

        // 3) 곡 길이를 모를 때만: 제목+아티스트 일치하는 첫 싱크 가사
        if (dur <= 0)
        {
            var any = usable.FirstOrDefault(c => TitleOk(c) && ArtistOk(c));
            if (any != null) return (any, 1.0);
        }
        return null;
    }

    // ───────────── LRCLIB ─────────────

    static async Task<List<Candidate>> LrclibGetAsync(string title, string artist, string album, double dur, CancellationToken ct)
    {
        var json = await GetStringAsync("https://lrclib.net/api/get?" +
                                        $"track_name={Uri.EscapeDataString(title)}" +
                                        $"&artist_name={Uri.EscapeDataString(artist)}" +
                                        $"&album_name={Uri.EscapeDataString(album)}" +
                                        $"&duration={Math.Round(dur)}", null, ct);
        return json == null ? new() : new() { LrclibCandidate(JsonDocument.Parse(json).RootElement, 0) };
    }

    static async Task<List<Candidate>> LrclibSearchAsync(string query, CancellationToken ct)
    {
        var json = await GetStringAsync("https://lrclib.net/api/search?" + query, null, ct);
        var list = new List<Candidate>();
        if (json == null) return list;
        var arr = JsonDocument.Parse(json).RootElement;
        if (arr.ValueKind != JsonValueKind.Array) return list;
        int rank = 0;
        foreach (var e in arr.EnumerateArray()) list.Add(LrclibCandidate(e, rank++));
        return list;
    }

    static Candidate LrclibCandidate(JsonElement e, int rank) => new(
        "lrclib",
        Str(e, "trackName"),
        Str(e, "artistName"),
        e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0,
        Str(e, "syncedLyrics"),
        e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True,
        rank);

    // ───────────── NetEase ─────────────

    static readonly Dictionary<string, string> NetEaseHeaders = new()
    {
        ["Referer"] = "https://music.163.com/",
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36",
    };

    static async Task<List<Candidate>> NetEaseAsync(string query, CancellationToken ct)
    {
        var json = await GetStringAsync(
            $"https://music.163.com/api/search/get?s={Uri.EscapeDataString(query)}&type=1&limit=6", NetEaseHeaders, ct);
        var list = new List<Candidate>();
        if (json == null) return list;
        var root = JsonDocument.Parse(json).RootElement;
        if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs)) return list;

        // 상위 3곡만 가사 조회 (동시에)
        var top = songs.EnumerateArray().Take(3).Select((s, rank) => (s: s.Clone(), rank)).ToList();
        var lyricTasks = top.Select(async x =>
        {
            long id = x.s.GetProperty("id").GetInt64();
            var lj = await GetStringAsync($"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1", NetEaseHeaders, ct);
            string lrc = "";
            bool instrumental = false;
            if (lj != null)
            {
                var lr = JsonDocument.Parse(lj).RootElement;
                if (lr.TryGetProperty("lrc", out var l) && l.TryGetProperty("lyric", out var t) && t.ValueKind == JsonValueKind.String)
                    lrc = t.GetString() ?? "";
                instrumental = (lr.TryGetProperty("pureMusic", out var pm) && pm.ValueKind == JsonValueKind.True) ||
                               lrc.Contains("纯音乐，请欣赏");
            }
            string artistName = x.s.TryGetProperty("artists", out var ar) && ar.GetArrayLength() > 0
                ? Str(ar[0], "name") : "";
            double durSec = x.s.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() / 1000 : 0;
            return new Candidate("netease", Str(x.s, "name"), artistName, durSec,
                instrumental ? "" : (TimeTag.IsMatch(lrc) ? lrc : ""), instrumental, x.rank);
        });
        list.AddRange(await Task.WhenAll(lyricTasks));
        return list;
    }

    // ───────────── 캐시 ─────────────

    sealed record CacheEntry(int V, DateTime SavedAt, string Status, List<LyricLine> Lines);

    string CachePath(string key) =>
        Path.Combine(_cacheDir, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(key))) + ".json");

    bool TryCache(string key, out LyricsResult result)
    {
        lock (_memory)
            if (_memory.TryGetValue(key, out result!)) return true;
        try
        {
            var path = CachePath(key);
            if (File.Exists(path))
            {
                var e = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path));
                bool expired = e == null || e.V != CacheVersion ||
                               (e.Status != "found" && DateTime.UtcNow - e.SavedAt > NegativeTtl);
                if (!expired)
                {
                    result = new LyricsResult(e!.Status, e.Lines);
                    lock (_memory) _memory[key] = result;
                    return true;
                }
            }
        }
        catch (Exception ex) { Log.Write("lyrics cache read: " + ex.Message); }
        result = None;
        return false;
    }

    void Store(string key, LyricsResult r)
    {
        lock (_memory) _memory[key] = r;
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var e = new CacheEntry(CacheVersion, DateTime.UtcNow, r.Status, r.Lines.ToList());
            File.WriteAllText(CachePath(key), JsonSerializer.Serialize(e));
        }
        catch (Exception ex) { Log.Write("lyrics cache write: " + ex.Message); }
    }

    // ───────────── 요청 ─────────────

    // 404 → null. 5xx·429·타임아웃·네트워크 오류는 짧게 재시도 후 LyricsUnavailableException.
    static async Task<string?> GetStringAsync(string url, Dictionary<string, string>? headers, CancellationToken ct)
    {
        string last = "";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(6000);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (headers != null)
                    foreach (var (k, v) in headers)
                    {
                        if (k == "User-Agent") req.Headers.UserAgent.Clear();
                        req.Headers.TryAddWithoutValidation(k, v);
                    }
                using var res = await Http.Client.SendAsync(req, cts.Token);
                if (res.StatusCode == HttpStatusCode.NotFound) return null;
                if (res.IsSuccessStatusCode) return await res.Content.ReadAsStringAsync(cts.Token);
                last = $"HTTP {(int)res.StatusCode}";
                if ((int)res.StatusCode < 500 && res.StatusCode != HttpStatusCode.TooManyRequests)
                    throw new LyricsUnavailableException(last); // 재시도해도 소용없는 오류
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { last = "timeout"; }
            catch (HttpRequestException ex) { last = ex.Message; }
            catch (JsonException ex) { last = "bad json: " + ex.Message; }

            if (attempt >= RequestRetryDelays.Length) throw new LyricsUnavailableException(last);
            await Task.Delay(RequestRetryDelays[attempt], ct);
        }
    }

    // ───────────── 변환 ─────────────

    // "(feat. X)", "- Remastered 2011", "(Sped Up)", "[Live]" 등 제거
    static string CleanTitle(string t)
    {
        string s = Regex.Replace(t, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        s = Regex.Replace(s, @"\s+-\s+.*$", "");
        s = Regex.Replace(s, @"\s+(feat\.?|ft\.?|with)\s+.*$", "", RegexOptions.IgnoreCase);
        s = s.Trim();
        return s.Length > 0 ? s : t;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static List<LyricLine> ParseLrc(string lrc, double scale)
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
        lines.Sort((a, b) => a.T.CompareTo(b.T));
        return lines;
    }
}
