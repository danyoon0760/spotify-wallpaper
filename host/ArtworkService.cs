using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpotifyWallpaper;

// 고해상도 앨범아트 찾기 (Spotify가 주는 SMTC 썸네일은 300×300이라 1440p에서 흐림)
//  - 이미 받은 곡은 디스크(ArtStore)에서 바로 꺼내고 검색하지 않는다
//  - 1차: iTunes US(곡·앨범, 1200px) + Deezer(곡, 1000px)를 동시에 조회
//    2차: 제목이 정확히 맞는 결과가 없을 때만 iTunes JP (일본어 제목은 JP 스토어에만 원문으로 있음)
//  - 제목이 괄호까지 정확히 같은 결과(예: "(SPED UP)")를 우선, 그다음 비슷한 제목
//  ※ iTunes KR 스토어는 검색 API가 어떤 검색어에도 0건을 돌려줘서 뺐다 (2026-11 확인)
//  ※ iTunes 검색은 IP당 분당 20회 정도로 제한되므로 요청 수를 아낀다
internal sealed class ArtworkService
{
    const int ExactScore = 10;          // 제목 정확히 일치(6) + 아티스트 일치(4)
    const long MaxImageBytes = 15_000_000;
    static readonly Regex Brackets = new(@"\s*[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);

    readonly ArtStore _store;

    public ArtworkService(ArtStore store) => _store = store;

    sealed record Hit(string Source, string Url, int Score);

    // 검색 한 번(곡 하나)의 요청 실패 수. 실패가 있었으면 "없음"으로 기억하지 않는다
    sealed class Attempt { public int Failures; }

    // 저장소의 파일 이름을 돌려준다 (못 찾으면 null)
    public async Task<string?> FindAsync(TrackInfo track, CancellationToken ct)
    {
        string id = ArtStore.Id(track.Key);
        if (_store.Find(ArtStore.HiRes, id) is { } cached) return cached;
        if (_store.IsKnownMissing(ArtStore.HiRes, id)) return null;

        try
        {
            string artist = Http.FirstArtist(track.Artist);
            bool hasAlbum = !string.IsNullOrWhiteSpace(track.Album);
            var attempt = new Attempt();

            async Task<List<Hit>> Search(params Task<List<Hit>>[] searches) =>
                (await Task.WhenAll(searches)).SelectMany(h => h).ToList();

            var hits = await Search(
                ITunesAsync("song", "US", artist, track.Title, track, attempt, ct),
                hasAlbum ? ITunesAsync("album", "US", artist, track.Album, track, attempt, ct) : Task.FromResult(new List<Hit>()),
                DeezerAsync(artist, track, attempt, ct));

            if (!hits.Any(h => h.Score >= ExactScore))
            {
                hits.AddRange(await Search(
                    ITunesAsync("song", "JP", artist, track.Title, track, attempt, ct),
                    hasAlbum ? ITunesAsync("album", "JP", artist, track.Album, track, attempt, ct) : Task.FromResult(new List<Hit>())));
            }

            var best = hits.OrderByDescending(h => h.Score).FirstOrDefault();
            if (best == null)
            {
                // 요청이 전부 성공했는데도 없을 때만 "없음"으로 기억 (일시 오류면 다음에 다시 검색)
                if (attempt.Failures == 0) _store.MarkMissing(ArtStore.HiRes, id);
                Log.Write($"artwork '{track.Title}': not found{(attempt.Failures > 0 ? $" ({attempt.Failures} request(s) failed)" : "")}, using Spotify thumbnail");
                return null;
            }

            byte[] bytes = await DownloadAsync(best.Url, ct);
            string? name = _store.Save(ArtStore.HiRes, id, bytes);
            Log.Write(name != null
                ? $"artwork '{track.Title}': {best.Source} (score {best.Score}, {bytes.Length / 1024}KB)"
                : $"artwork '{track.Title}': {best.Source} returned something that is not an image");
            return name;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Write($"artwork '{track.Title}': {ex.Message}");
            return null;
        }
    }

    static async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        using var res = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        if (res.Content.Headers.ContentLength is > MaxImageBytes)
            throw new InvalidOperationException("image too large");
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    // 점수 (-1 = 탈락): 제목 정확히 일치 6 / 비슷 2, 아티스트 일치 필수 +4, 앨범 일치 +2, iTunes(1200px) +1
    internal static int Score(string title, string wanted, string artistName, string artist, string albumName, string album, bool itunes)
    {
        bool exact = string.Equals(title.Trim(), wanted.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!exact && !Http.Similar(title, wanted)) return -1;
        bool artistOk = artist.Length == 0 || Http.Similar(artistName, artist);
        if (!artistOk) return -1;
        int s = (exact ? 6 : 2) + 4;
        if (album.Length > 0 && Http.Similar(albumName, album)) s += 2;
        if (itunes) s += 1;
        return s;
    }

    static async Task<List<Hit>> ITunesAsync(string entity, string country, string artist, string term, TrackInfo track, Attempt attempt, CancellationToken ct)
    {
        var hits = new List<Hit>();
        try
        {
            var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString($"{artist} {term}".Trim())}&entity={entity}&limit=10&country={country}";
            using var res = await Http.Client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                // 403·429는 대개 호출 제한
                Interlocked.Increment(ref attempt.Failures);
                Log.Write($"itunes {entity}/{country}: HTTP {(int)res.StatusCode}");
                return hits;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return hits;
            foreach (var r in results.EnumerateArray())
            {
                string art = Str(r, "artworkUrl100");
                if (art.Length == 0) continue;
                int score = entity == "song"
                    ? Score(Str(r, "trackName"), track.Title, Str(r, "artistName"), artist, Str(r, "collectionName"), track.Album, true)
                    : Score(Str(r, "collectionName"), track.Album, Str(r, "artistName"), artist, "", "", true);
                if (score > 0) hits.Add(new Hit($"itunes-{entity}-{country}", art.Replace("100x100bb", "1200x1200bb"), score));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Interlocked.Increment(ref attempt.Failures);
            Log.Write($"itunes {entity}/{country}: {ex.Message}");
        }
        return hits;
    }

    static async Task<List<Hit>> DeezerAsync(string artist, TrackInfo track, Attempt attempt, CancellationToken ct)
    {
        var hits = new List<Hit>();
        try
        {
            string cleanTitle = Brackets.Replace(track.Title, "").Trim();
            string q = artist.Length > 0 ? $"artist:\"{artist}\" track:\"{cleanTitle}\"" : track.Title;
            var results = await DeezerSearch(q, ct);
            if (results.Count == 0) results = await DeezerSearch($"{artist} {track.Title}".Trim(), ct);
            foreach (var r in results)
            {
                string cover = r.TryGetProperty("album", out var al) ? Str(al, "cover_xl") : "";
                if (cover.Length == 0) continue;
                string artistName = r.TryGetProperty("artist", out var ar) ? Str(ar, "name") : "";
                int score = Score(Str(r, "title"), track.Title, artistName, artist, Str(al, "title"), track.Album, false);
                if (score > 0) hits.Add(new Hit("deezer", cover, score));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Interlocked.Increment(ref attempt.Failures);
            Log.Write("deezer: " + ex.Message);
        }
        return hits;
    }

    static async Task<List<JsonElement>> DeezerSearch(string q, CancellationToken ct)
    {
        using var res = await Http.Client.GetAsync($"https://api.deezer.com/search?q={Uri.EscapeDataString(q)}&limit=10", ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(e => e.Clone()).ToList()
            : new List<JsonElement>();
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
