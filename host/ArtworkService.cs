using System.Text.Json;

namespace SpotifyWallpaper;

// 고해상도 앨범아트 찾기 (Spotify가 주는 SMTC 썸네일은 300×300이라 1440p에서 흐림)
//  - iTunes(곡·앨범 검색, 1200px)와 Deezer(곡 검색, 1000px)를 동시에 조회
//  - 제목이 괄호까지 정확히 같은 결과(예: "(SPED UP)")를 우선, 그다음 비슷한 제목
public sealed class ArtworkService
{
    static readonly string[] Countries = { "KR", "US", "JP" };
    readonly Dictionary<string, byte[]?> _cache = new();

    sealed record Hit(string Source, string Url, int Score);

    public async Task<byte[]?> FindAsync(TrackInfo track, CancellationToken ct)
    {
        lock (_cache)
            if (_cache.TryGetValue(track.Key, out var cached)) return cached;

        byte[]? found = null;
        try
        {
            string artist = Http.FirstArtist(track.Artist);
            var searches = new List<Task<List<Hit>>>();
            foreach (var c in Countries)
            {
                searches.Add(ITunesAsync("song", c, artist, track.Title, track, ct));
                if (!string.IsNullOrWhiteSpace(track.Album))
                    searches.Add(ITunesAsync("album", c, artist, track.Album, track, ct));
            }
            searches.Add(DeezerAsync(artist, track, ct));
            await Task.WhenAll(searches);

            var best = searches.SelectMany(s => s.Result).OrderByDescending(h => h.Score).FirstOrDefault();
            if (best != null)
            {
                found = await Http.Client.GetByteArrayAsync(best.Url, ct);
                Log.Write($"artwork '{track.Title}': {best.Source} (score {best.Score})");
            }
            else Log.Write($"artwork '{track.Title}': not found, using Spotify thumbnail");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Write("artwork: " + ex.Message);
            return null; // 일시 오류는 캐시하지 않음
        }

        lock (_cache) _cache[track.Key] = found;
        return found;
    }

    // 점수 (-1 = 탈락): 제목 정확히 일치 6 / 비슷 2, 아티스트 일치 필수 +4, 앨범 일치 +2, iTunes(1200px) +1
    static int Score(string title, string wanted, string artistName, string artist, string albumName, string album, bool itunes)
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

    async Task<List<Hit>> ITunesAsync(string entity, string country, string artist, string term, TrackInfo track, CancellationToken ct)
    {
        var hits = new List<Hit>();
        try
        {
            var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString($"{artist} {term}")}&entity={entity}&limit=10&country={country}";
            using var res = await Http.Client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return hits;
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
            if (!root.TryGetProperty("results", out var results)) return hits;
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
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Write($"itunes {entity}/{country}: {ex.Message}"); }
        return hits;
    }

    async Task<List<Hit>> DeezerAsync(string artist, TrackInfo track, CancellationToken ct)
    {
        var hits = new List<Hit>();
        try
        {
            string cleanTitle = System.Text.RegularExpressions.Regex.Replace(track.Title, @"\s*[\(\[][^\)\]]*[\)\]]", "").Trim();
            string q = artist.Length > 0 ? $"artist:\"{artist}\" track:\"{cleanTitle}\"" : track.Title;
            var root = await DeezerSearch(q, ct);
            if (root.GetArrayLength() == 0) root = await DeezerSearch($"{artist} {track.Title}".Trim(), ct);
            foreach (var r in root.EnumerateArray())
            {
                string cover = r.TryGetProperty("album", out var al) ? Str(al, "cover_xl") : "";
                if (cover.Length == 0) continue;
                string artistName = r.TryGetProperty("artist", out var ar) ? Str(ar, "name") : "";
                int score = Score(Str(r, "title"), track.Title, artistName, artist, Str(al, "title"), track.Album, false);
                if (score > 0) hits.Add(new Hit("deezer", cover, score));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Write("deezer: " + ex.Message); }
        return hits;
    }

    static async Task<JsonElement> DeezerSearch(string q, CancellationToken ct)
    {
        using var res = await Http.Client.GetAsync($"https://api.deezer.com/search?q={Uri.EscapeDataString(q)}&limit=10", ct);
        var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
        return root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.Clone()
            : JsonDocument.Parse("[]").RootElement;
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
