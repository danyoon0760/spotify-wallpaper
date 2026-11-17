using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace SpotifyWallpaper.Tests;

static class TestSetup
{
    // 테스트가 실제 로그 파일(%APPDATA%\SpotifyWallpaper\log.txt)에 쓰지 않게 한다
    [ModuleInitializer]
    public static void Init() => Log.Enabled = false;
}

// 제목·아티스트 비교 (가사와 앨범아트 검색이 함께 쓰는 기준)
public class HttpTests
{
    [Theory]
    [InlineData("Blueming (Live)", "blueming")]
    [InlineData("Song [Remastered]", "song")]
    [InlineData("Song feat. Someone", "song")]
    [InlineData("Song - Radio Edit", "song")]
    [InlineData("Die With A Smile", "diewithasmile")]
    [InlineData("ＡＢＣ　１２３", "abc123")]          // 전각 문자는 반각으로
    [InlineData("夜に駆ける", "夜に駆ける")]
    [InlineData("(G)I-DLE", "idle")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Norm_reduces_to_comparable_form(string? input, string expected) =>
        Assert.Equal(expected, Http.Norm(input));

    [Theory]
    [InlineData("Blueming", "blueming (Live)", true)]
    [InlineData("Song Title", "Song Title Remastered", true)]   // 한쪽이 다른 쪽을 포함
    [InlineData("YOASOBI", "YOASOBI, Ayase", true)]
    [InlineData("Die", "Die Young", false)]                     // 짧은 이름이 우연히 포함된 경우
    [InlineData("花", "花火", false)]
    [InlineData("Go", "Gone", false)]
    [InlineData("Up", "Up (feat. X)", true)]                    // 정규화하면 같아지는 건 길이와 무관하게 인정
    [InlineData("Song", "Other", false)]
    [InlineData("", "Song", false)]
    [InlineData("[Intro]", "[Intro]", false)]                   // 비교할 글자가 남지 않으면 판단하지 않음
    public void Similar_matches_same_name_in_different_spellings(string a, string b, bool expected)
    {
        Assert.Equal(expected, Http.Similar(a, b));
        Assert.Equal(expected, Http.Similar(b, a));
    }

    [Theory]
    [InlineData("IU", "IU")]
    [InlineData("A, B & C", "A")]
    [InlineData("YOASOBI、Ayase", "YOASOBI")]
    [InlineData("A x B", "A")]
    public void FirstArtist_takes_the_lead_artist(string input, string expected) =>
        Assert.Equal(expected, Http.FirstArtist(input));
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+L", "Ctrl+Alt+L")]
    [InlineData("ctrl + alt + l", "Ctrl+Alt+L")]
    [InlineData("Alt+Ctrl+L", "Ctrl+Alt+L")]           // 표기 순서는 정해진 대로
    [InlineData("Control+Shift+F9", "Ctrl+Shift+F9")]
    [InlineData("Alt+1", "Alt+1")]
    [InlineData("Win+Shift+K", "Shift+Win+K")]
    public void Parses_and_formats(string text, string expected)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(expected, hk.ToString());
        // 표시한 글자를 다시 읽어도 같은 조합
        Assert.True(Hotkey.TryParse(hk.ToString(), out var again));
        Assert.Equal(hk, again);
    }

    [Theory]
    [InlineData("")]                 // 사용 안 함
    [InlineData(null)]
    [InlineData("L")]                // 조합 키 없음
    [InlineData("Shift+L")]          // Shift만으로는 일반 입력을 가로챔
    [InlineData("Ctrl+Alt")]         // 일반 키 없음
    [InlineData("Ctrl+A+B")]         // 일반 키 둘
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+123")]
    [InlineData("Ctrl+Shift")]       // 조합 키끼리
    public void Rejects_invalid_or_empty(string? text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Parsed_keys_map_to_win32_values()
    {
        Assert.True(Hotkey.TryParse("Ctrl+Alt+L", out var hk));
        Assert.Equal(Native.MOD_CONTROL | Native.MOD_ALT, hk.Modifiers);
        Assert.Equal(Keys.L, hk.Key);
    }

    [Fact]
    public void FromKeyData_reads_a_pressed_combination()
    {
        var hk = Hotkey.FromKeyData(Keys.Control | Keys.Alt | Keys.L);
        Assert.Equal("Ctrl+Alt+L", hk!.Value.ToString());
        Assert.True(hk.Value.HasRequiredModifier);

        Assert.Null(Hotkey.FromKeyData(Keys.Control | Keys.ControlKey));   // 아직 조합 키만 누른 상태
        Assert.False(Hotkey.FromKeyData(Keys.Shift | Keys.L)!.Value.HasRequiredModifier);
    }
}

public class HotkeyDialogTests
{
    [Theory]
    [InlineData("Ctrl+Alt+L", "Ctrl+Alt+L")]
    [InlineData("", "사용 안 함")]
    [InlineData("nonsense", "사용 안 함")]
    public void Shows_the_current_setting(string current, string shown)
    {
        using var dialog = new HotkeyDialog(current);
        Assert.Contains(dialog.Controls.OfType<Label>(), l => l.Text == shown);
        // 조합을 누르기 전에는 확인 버튼이 꺼져 있고, 기본 포커스는 취소 (Enter로 설정이 바뀌지 않게)
        Assert.False(dialog.Controls.OfType<Button>().Single(b => b.Text == "확인").Enabled);
        Assert.Equal("취소", (dialog.ActiveControl as Button)?.Text);
    }
}

public class ArtworkScoreTests
{
    [Fact]
    public void Exact_title_beats_similar_title()
    {
        int exact = ArtworkService.Score("Song (Sped Up)", "Song (Sped Up)", "Artist", "Artist", "", "", itunes: true);
        int similar = ArtworkService.Score("Song", "Song (Sped Up)", "Artist", "Artist", "", "", itunes: true);
        Assert.True(exact > similar);
        Assert.True(similar > 0);
    }

    [Fact]
    public void Wrong_artist_or_title_is_rejected()
    {
        Assert.Equal(-1, ArtworkService.Score("Song", "Song", "Someone Else", "Artist", "", "", itunes: true));
        Assert.Equal(-1, ArtworkService.Score("Other", "Song", "Artist", "Artist", "", "", itunes: true));
    }

    [Fact]
    public void Album_match_and_itunes_add_points()
    {
        int plain = ArtworkService.Score("Song", "Song", "Artist", "Artist", "Greatest Hits", "Album", itunes: false);
        int withAlbum = ArtworkService.Score("Song", "Song", "Artist", "Artist", "Album", "Album", itunes: false);
        int withAlbumItunes = ArtworkService.Score("Song", "Song", "Artist", "Artist", "Album", "Album", itunes: true);
        Assert.Equal(10, plain);
        Assert.Equal(12, withAlbum);
        Assert.Equal(13, withAlbumItunes);
    }
}

public sealed class ArtStoreTests : IDisposable
{
    static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
    static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 };

    readonly string _dir = Path.Combine(Path.GetTempPath(), "SpotifyWallpaper.Tests", Guid.NewGuid().ToString("N"));
    readonly ArtStore _store;

    public ArtStoreTests() => _store = new ArtStore(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Detects_image_format_from_content()
    {
        Assert.Equal(".jpg", ArtStore.ExtensionOf(Jpeg));
        Assert.Equal(".png", ArtStore.ExtensionOf(Png));
        Assert.Equal(".webp", ArtStore.ExtensionOf("RIFF\0\0\0\0WEBPVP8 "u8.ToArray()));
        Assert.Null(ArtStore.ExtensionOf("<html>not an image</html>"u8.ToArray()));
        Assert.Null(ArtStore.ExtensionOf(Array.Empty<byte>()));
    }

    [Fact]
    public void Saves_finds_and_serves_a_file()
    {
        string id = ArtStore.Id("Song|Artist|Album");
        Assert.Null(_store.Find(ArtStore.HiRes, id));

        string? name = _store.Save(ArtStore.HiRes, id, Jpeg);
        Assert.Equal($"hi-{id}.jpg", name);
        Assert.Equal(name, _store.Find(ArtStore.HiRes, id));

        var file = _store.Read(name!);
        Assert.Equal(Jpeg, file!.Value.Bytes);
        Assert.Equal("image/jpeg", file.Value.Mime);
        Assert.Equal($"https://{ArtStore.Host}/{name}", ArtStore.Url(name!));
    }

    [Fact]
    public void Refuses_to_save_non_images()
    {
        Assert.Null(_store.Save(ArtStore.HiRes, ArtStore.Id("x"), "error page"u8.ToArray()));
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir).Any());
    }

    [Theory]
    [InlineData("../settings.json")]
    [InlineData("..\\..\\secret.jpg")]
    [InlineData("hi-short.jpg")]
    [InlineData("xx-0123456789abcdef0123456789abcdef01234567.jpg")]
    [InlineData("hi-0123456789abcdef0123456789abcdef01234567.exe")]
    [InlineData("")]
    public void Read_only_accepts_names_the_store_created(string name)
    {
        _store.Save(ArtStore.HiRes, ArtStore.Id("x"), Jpeg); // 폴더가 있는 상태에서도
        Assert.Null(_store.Read(name));
    }

    [Fact]
    public void Remembers_missing_artwork()
    {
        string id = ArtStore.Id("no art");
        Assert.False(_store.IsKnownMissing(ArtStore.HiRes, id));
        _store.MarkMissing(ArtStore.HiRes, id);
        Assert.True(_store.IsKnownMissing(ArtStore.HiRes, id));

        // 7일이 지나면 다시 검색
        File.SetLastWriteTimeUtc(Path.Combine(_dir, $"hi-{id}.none"), DateTime.UtcNow.AddDays(-8));
        Assert.False(_store.IsKnownMissing(ArtStore.HiRes, id));
    }

    [Fact]
    public void Prune_removes_only_old_surplus_files()
    {
        // 썸네일 한도(40)를 넘겨 저장: 오래된 것부터 지워지고, 방금 쓴 파일은 한도를 넘어도 남는다
        var old = Enumerable.Range(0, 45).Select(i => ArtStore.Id("old" + i)).ToList();
        foreach (var id in old)
        {
            string name = _store.Save(ArtStore.Thumb, id, Png)!;
            File.SetLastWriteTimeUtc(Path.Combine(_dir, name), DateTime.UtcNow.AddDays(-2).AddMinutes(old.IndexOf(id)));
        }
        string fresh = ArtStore.Id("fresh");
        _store.Save(ArtStore.Thumb, fresh, Png);

        _store.Prune();

        Assert.NotNull(_store.Find(ArtStore.Thumb, fresh));
        Assert.Equal(40, Directory.EnumerateFiles(_dir, "th-*").Count());
        Assert.Null(_store.Find(ArtStore.Thumb, old[0]));       // 가장 오래된 것은 지워짐
        Assert.NotNull(_store.Find(ArtStore.Thumb, old[44]));   // 최근 것은 남음
    }
}
