using System.Net;
using System.Text.Json;

namespace SpotifyWallpaper;

public sealed record LyricLine(double T, string Text);
public sealed record LyricsResult(string Status, IReadOnlyList<LyricLine> Lines);

// 서버 오류 등으로 "가사가 있는지 없는지" 판단하지 못했을 때. (가사 없음과 구분)
public sealed class LyricsUnavailableException(string message) : Exception(message);

// 싱크 가사 검색: LRCLIB + NetEase (어떤 후보를 고를지는 LyricsMatcher)
//  - LRCLIB은 한 번에 하나씩, 정확한 조회 → 검색 → 넓은 검색 순서로 필요한 만큼만 요청한다
//    (예전처럼 3~4개를 한꺼번에 보내면 429 = 요청 과다에 자주 걸림)
//  - NetEase는 서버가 달라 LRCLIB과 동시에 조회
//  - 429는 서버가 알려 준 시간만큼 쉬고, 5xx·타임아웃은 한 번만 다시 시도, 그 밖의 4xx는 "결과 없음"으로 처리
//  - 일시 오류로 판단할 수 없으면 LyricsUnavailableException → 호출한 쪽이 나중에 다시 시도
//  - 결과는 디스크에 저장 ("가사 없음"은 하루 뒤 다시 확인)
public sealed class LyricsService
{
    const int CacheVersion = 3;
    const int RequestTimeoutMs = 6000;
    const int RetryDelayMs = 700;
    static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(1); // 검색 결과가 가끔 흔들려서 "없음"은 짧게만 기억
    static readonly LyricsResult None = new("none", Array.Empty<LyricLine>());

    readonly Dictionary<string, LyricsResult> _memory = new();
    readonly string _cacheDir = AppPaths.LyricsCache;

    // LRCLIB 요청은 한 번에 하나. 429를 받으면 _lrclibResumeAt(TickCount64)까지 쉰다
    readonly SemaphoreSlim _lrclibGate = new(1, 1);
    long _lrclibResumeAt;

    // 다시 시도할 때 NetEase까지 또 부르지 않도록, 마지막 곡의 NetEase 결과는 기억해 둔다
    (string Key, List<LyricCandidate> Candidates)? _neteaseMemo;

    // 판단할 수 없으면 LyricsUnavailableException
    public async Task<LyricsResult> GetAsync(TrackInfo track, double durationMs, CancellationToken ct)
    {
        if (TryCache(track.Key, out var cached)) return cached;
        var result = await FetchAsync(track, durationMs, ct);
        // 곡 길이를 모르는 채로 내린 "없음"은 믿을 수 없으므로 기억하지 않음
        if (result.Status == "found" || durationMs > 0) Store(track.Key, result);
        return result;
    }

    async Task<LyricsResult> FetchAsync(TrackInfo track, double durationMs, CancellationToken ct)
    {
        var q = LyricQuery.From(track, durationMs);
        var candidates = new List<LyricCandidate>();
        int failures = 0;

        // 실패한 요청은 null로 돌려주고 개수만 센다 (나머지 요청 결과는 살림)
        async Task<List<LyricCandidate>?> Try(string name, Func<Task<List<LyricCandidate>>> f)
        {
            try { return await f(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Write($"lyrics {name} failed: {ex.Message}");
                return null;
            }
        }

        async Task<bool> Add(Task<List<LyricCandidate>?> source)
        {
            var list = await source;
            if (list != null) candidates.AddRange(list);
            return list != null;
        }

        static string Enc(string s) => Uri.EscapeDataString(s);

        // NetEase는 처음부터 같이 돌려 두고, LRCLIB은 차례대로
        var netease = Try("netease", () => NetEaseCachedAsync(track.Key, $"{q.Clean} {q.Artist}", ct));

        (LyricCandidate best, double scale)? pick = null;
        if (q.Duration > 0 && !string.IsNullOrWhiteSpace(q.Album))
        {
            await Add(Try("lrclib-get", () => LrclibGetAsync(q, ct)));
            pick = LyricsMatcher.Pick(candidates, q, exactOnly: true);
        }
        if (pick == null)
        {
            await Add(Try("lrclib-search", () => LrclibSearchAsync($"track_name={Enc(q.Title)}&artist_name={Enc(q.Artist)}", ct)));
            pick = LyricsMatcher.Pick(candidates, q, exactOnly: true);
        }
        if (pick == null && !q.Clean.Equals(q.Title, StringComparison.OrdinalIgnoreCase))
        {
            await Add(Try("lrclib-clean", () => LrclibSearchAsync($"track_name={Enc(q.Clean)}&artist_name={Enc(q.Artist)}", ct)));
            pick = LyricsMatcher.Pick(candidates, q, exactOnly: true);
        }

        // 길이가 딱 맞는 LRCLIB 가사를 못 찾았으면 NetEase까지 합쳐서 전체 규칙(배속 보정 포함)으로 다시 고른다
        if (pick == null)
        {
            await Add(netease);
            pick = LyricsMatcher.Pick(candidates, q);
        }
        if (pick == null && failures == 0)
        {
            await Add(Try("lrclib-wide", () => LrclibSearchAsync($"q={Enc(q.Clean + " " + q.Artist)}", ct)));
            pick = LyricsMatcher.Pick(candidates, q);
        }

        if (pick == null)
        {
            if (failures > 0) throw new LyricsUnavailableException($"{failures} request(s) failed, no match");
            // 모든 요청이 성공했는데 맞는 싱크 가사 없음 → 가사 없음. 가까웠던 후보를 남겨 기준 조정의 근거로 쓴다
            Log.Write($"lyrics '{q.Title}' ({q.Duration:F0}s): no synced lyrics within ±{LyricsMatcher.DurationTolerance}s ({candidates.Count} candidates)");
            foreach (var c in LyricsMatcher.Nearest(candidates, q, 3))
                Log.Write("    near miss " + LyricsMatcher.Describe(c, q));
            return None;
        }

        var (best, scale) = pick.Value;
        Log.Write($"lyrics '{q.Title}' → [{best.Source}] '{best.TrackName}' ({best.ArtistName}) dur {q.Duration:F1}s vs {best.Duration:F1}s scale {scale:F3}");
        if (best.Instrumental) return new("instrumental", Array.Empty<LyricLine>());
        var lines = LyricsMatcher.ParseLrc(best.Synced, scale);
        return lines.Count(l => l.Text.Length > 0) >= 2 ? new("found", lines) : None;
    }

    // ───────────── LRCLIB ─────────────

    async Task<List<LyricCandidate>> LrclibGetAsync(LyricQuery q, CancellationToken ct)
    {
        var json = await GetStringAsync("https://lrclib.net/api/get?" +
                                        $"track_name={Uri.EscapeDataString(q.Title)}" +
                                        $"&artist_name={Uri.EscapeDataString(q.Artist)}" +
                                        $"&album_name={Uri.EscapeDataString(q.Album)}" +
                                        $"&duration={Math.Round(q.Duration)}", lrclib: true, ct);
        if (json == null) return new();
        using var doc = JsonDocument.Parse(json);
        return new() { LrclibCandidate(doc.RootElement, 0) };
    }

    async Task<List<LyricCandidate>> LrclibSearchAsync(string query, CancellationToken ct)
    {
        var json = await GetStringAsync("https://lrclib.net/api/search?" + query, lrclib: true, ct);
        var list = new List<LyricCandidate>();
        if (json == null) return list;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        int rank = 0;
        foreach (var e in doc.RootElement.EnumerateArray()) list.Add(LrclibCandidate(e, rank++));
        return list;
    }

    static LyricCandidate LrclibCandidate(JsonElement e, int rank) => new(
        "lrclib",
        Str(e, "trackName"),
        Str(e, "artistName"),
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0,
        Str(e, "syncedLyrics"),
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True,
        rank);

    // ───────────── NetEase ─────────────

    static readonly Dictionary<string, string> NetEaseHeaders = new()
    {
        ["Referer"] = "https://music.163.com/",
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36",
    };

    async Task<List<LyricCandidate>> NetEaseCachedAsync(string trackKey, string query, CancellationToken ct)
    {
        if (_neteaseMemo is { } memo && memo.Key == trackKey) return memo.Candidates;
        var list = await NetEaseAsync(query, ct);
        _neteaseMemo = (trackKey, list);
        return list;
    }

    async Task<List<LyricCandidate>> NetEaseAsync(string query, CancellationToken ct)
    {
        var json = await GetStringAsync(
            $"https://music.163.com/api/search/get?s={Uri.EscapeDataString(query)}&type=1&limit=6", lrclib: false, ct);
        var list = new List<LyricCandidate>();
        if (json == null) return list;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array) return list;

        // 상위 3곡만 가사 조회 (동시에)
        var top = songs.EnumerateArray().Take(3).Select((s, rank) => (s: s.Clone(), rank)).ToList();
        var lyricTasks = top.Select(async x =>
        {
            long id = x.s.GetProperty("id").GetInt64();
            var lj = await GetStringAsync($"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1", lrclib: false, ct);
            string lrc = "";
            bool instrumental = false;
            if (lj != null)
            {
                using var ldoc = JsonDocument.Parse(lj);
                var lr = ldoc.RootElement;
                if (lr.TryGetProperty("lrc", out var l) && l.ValueKind == JsonValueKind.Object &&
                    l.TryGetProperty("lyric", out var t) && t.ValueKind == JsonValueKind.String)
                    lrc = t.GetString() ?? "";
                instrumental = (lr.TryGetProperty("pureMusic", out var pm) && pm.ValueKind == JsonValueKind.True) ||
                               lrc.Contains("纯音乐，请欣赏");
            }
            string artistName = x.s.TryGetProperty("artists", out var ar) && ar.ValueKind == JsonValueKind.Array && ar.GetArrayLength() > 0
                ? Str(ar[0], "name") : "";
            double durSec = x.s.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() / 1000 : 0;
            return new LyricCandidate("netease", Str(x.s, "name"), artistName, durSec,
                instrumental ? "" : (LyricsMatcher.HasTimeTags(lrc) ? lrc : ""), instrumental, x.rank);
        });
        list.AddRange(await Task.WhenAll(lyricTasks));
        return list;
    }

    // ───────────── 캐시 ─────────────

    sealed record CacheEntry(int V, DateTime SavedAt, string Status, List<LyricLine> Lines);

    string CachePath(string key) => Path.Combine(_cacheDir, ArtStore.Id(key).ToUpperInvariant() + ".json");

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
                    Remember(key, result);
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
        Remember(key, r);
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var e = new CacheEntry(CacheVersion, DateTime.UtcNow, r.Status, r.Lines.ToList());
            File.WriteAllText(CachePath(key), JsonSerializer.Serialize(e));
        }
        catch (Exception ex) { Log.Write("lyrics cache write: " + ex.Message); }
    }

    // 메모리에는 최근 곡만 (나머지는 디스크에서 다시 읽으면 됨)
    void Remember(string key, LyricsResult r)
    {
        lock (_memory)
        {
            if (_memory.Count >= 50) _memory.Clear();
            _memory[key] = r;
        }
    }

    // ───────────── 요청 ─────────────

    // 본문을 돌려준다. 404나 고쳐 보낼 수 없는 4xx는 null("결과 없음").
    // 429·5xx·타임아웃·네트워크 오류는 LyricsUnavailableException (5xx·타임아웃·네트워크 오류는 한 번 다시 시도한 뒤).
    async Task<string?> GetStringAsync(string url, bool lrclib, CancellationToken ct)
    {
        string last = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(RetryDelayMs, ct);
            if (lrclib) await _lrclibGate.WaitAsync(ct);
            try
            {
                if (lrclib) await WaitForLrclibCooldownAsync(ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RequestTimeoutMs);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!lrclib)
                    foreach (var (k, v) in NetEaseHeaders)
                    {
                        if (k == "User-Agent") req.Headers.UserAgent.Clear();
                        req.Headers.TryAddWithoutValidation(k, v);
                    }
                using var res = await Http.Client.SendAsync(req, cts.Token);
                int code = (int)res.StatusCode;
                if (res.IsSuccessStatusCode) return await res.Content.ReadAsStringAsync(cts.Token);
                if (res.StatusCode == HttpStatusCode.NotFound) return null;
                if (res.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    // 바로 다시 보내면 제한만 길어진다. 서버가 알려 준 시간(없으면 10초)만큼 쉰다
                    double wait = res.Headers.RetryAfter?.Delta?.TotalSeconds ?? 10;
                    wait = Math.Clamp(wait, 5, 60);
                    if (lrclib) Interlocked.Exchange(ref _lrclibResumeAt, Environment.TickCount64 + (long)(wait * 1000));
                    throw new LyricsUnavailableException($"HTTP 429, backing off {wait:F0}s");
                }
                if (code >= 500 || res.StatusCode == HttpStatusCode.RequestTimeout) last = $"HTTP {code}";
                else
                {
                    // 400 등: 같은 요청을 다시 보내도 결과가 같다 → 이 요청은 결과 없음으로 치고 넘어감
                    Log.Write($"lyrics request rejected: HTTP {code} {new Uri(url).Host}{new Uri(url).AbsolutePath}");
                    return null;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { last = "timeout"; }
            catch (HttpRequestException ex) { last = ex.Message; }
            finally
            {
                if (lrclib) _lrclibGate.Release();
            }
        }
        throw new LyricsUnavailableException(last);
    }

    // 쉬는 시간이 조금 남았으면 기다리고, 길게 남았으면 이번 시도는 포기 (호출한 쪽이 나중에 다시 시도)
    async Task WaitForLrclibCooldownAsync(CancellationToken ct)
    {
        long wait = Interlocked.Read(ref _lrclibResumeAt) - Environment.TickCount64;
        if (wait > 3000) throw new LyricsUnavailableException($"lrclib rate-limited, {wait / 1000}s left");
        if (wait > 0) await Task.Delay((int)wait, ct);
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
