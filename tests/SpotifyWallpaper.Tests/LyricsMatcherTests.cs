namespace SpotifyWallpaper.Tests;

// 가사 후보 고르기 규칙. 기준(허용 오차 등)을 바꿀 땐 여기 기대값도 함께 검토할 것.
public class LyricsMatcherTests
{
    static LyricQuery Query(string title, string artist, double dur, string album = "Album") =>
        LyricQuery.From(new TrackInfo($"{title}|{artist}|{album}", title, artist, album, null, ""), dur * 1000);

    static LyricCandidate Cand(string title, string artist, double dur, string source = "lrclib", int rank = 0,
        string synced = "[00:01.00]a\n[00:02.00]b", bool instrumental = false) =>
        new(source, title, artist, dur, synced, instrumental, rank);

    // ───────────── CleanTitle ─────────────

    [Theory]
    [InlineData("Blueming", "Blueming")]
    [InlineData("Song (feat. Someone)", "Song")]
    [InlineData("Song [Live]", "Song")]
    [InlineData("Song - Remastered 2011", "Song")]
    [InlineData("bonanza! - Slowed", "bonanza!")]
    [InlineData("Song feat. Someone", "Song")]
    [InlineData("Song ft. Someone", "Song")]
    [InlineData("Die With A Smile", "Die With A Smile")]   // 제목 속 with는 그대로
    [InlineData("(Intro)", "(Intro)")]                     // 다 지워지면 원래 제목 유지
    public void CleanTitle_strips_decorations(string title, string expected) =>
        Assert.Equal(expected, LyricsMatcher.CleanTitle(title));

    [Theory]
    [InlineData("Song (Sped Up)", true)]
    [InlineData("Song - Slowed + Reverb", true)]
    [InlineData("Nightcore Mix", true)]
    [InlineData("노래 (2배속)", true)]
    [InlineData("Speed of Light", false)]
    [InlineData("Song", false)]
    public void IsSpeedVariant_detects_tempo_edits(string text, bool expected) =>
        Assert.Equal(expected, LyricsMatcher.IsSpeedVariant(text));

    // ───────────── Pick: 길이가 맞는 후보 ─────────────

    [Fact]
    public void Picks_candidate_with_matching_title_artist_and_duration()
    {
        var pick = LyricsMatcher.Pick(new[] { Cand("Blueming", "IU", 217.4) }, Query("Blueming", "IU", 217));
        Assert.NotNull(pick);
        Assert.Equal(1.0, pick.Value.scale);
    }

    [Fact]
    public void Rejects_everything_when_duration_is_off_by_more_than_tolerance()
    {
        var pick = LyricsMatcher.Pick(new[] { Cand("Blueming", "IU", 217 + 3.5) }, Query("Blueming", "IU", 217));
        Assert.Null(pick);
    }

    [Fact]
    public void Prefers_closest_duration_then_lrclib_over_netease()
    {
        var q = Query("Song", "Artist", 200);
        var closest = LyricsMatcher.Pick(new[]
        {
            Cand("Song", "Artist", 202, "lrclib"),
            Cand("Song", "Artist", 200.5, "netease"),
        }, q);
        Assert.Equal("netease", closest!.Value.best.Source);

        var tie = LyricsMatcher.Pick(new[]
        {
            Cand("Song", "Artist", 201, "netease"),
            Cand("Song", "Artist", 201, "lrclib"),
        }, q);
        Assert.Equal("lrclib", tie!.Value.best.Source);
    }

    [Theory]
    [InlineData(1.9, true)]    // 제목만 맞아도 2초 안이면 인정
    [InlineData(2.5, false)]   // 2초를 넘으면 아티스트까지 맞아야 함
    public void Title_only_match_needs_tighter_duration(double diff, bool expected)
    {
        var pick = LyricsMatcher.Pick(new[] { Cand("Song", "Someone Else", 200 + diff) }, Query("Song", "Artist", 200));
        Assert.Equal(expected, pick != null);
    }

    [Theory]
    [InlineData(1.4, true)]    // 아티스트만 맞으면 1.5초 안
    [InlineData(2.0, false)]
    public void Artist_only_match_needs_tightest_duration(double diff, bool expected)
    {
        var pick = LyricsMatcher.Pick(new[] { Cand("Different Title", "Artist", 200 + diff) }, Query("Song", "Artist", 200));
        Assert.Equal(expected, pick != null);
    }

    [Fact]
    public void NetEase_top_results_are_trusted_on_duration_alone_but_lrclib_is_not()
    {
        var q = Query("夜に駆ける", "YOASOBI", 261);
        // 표기가 전혀 다른(번역·로마자) 제목이라도 NetEase 상위 3개 + 길이 일치면 인정
        Assert.NotNull(LyricsMatcher.Pick(new[] { Cand("Yoru ni Kakeru", "ヨアソビ", 261.3, "netease", rank: 2) }, q));
        Assert.Null(LyricsMatcher.Pick(new[] { Cand("Yoru ni Kakeru", "ヨアソビ", 261.3, "netease", rank: 3) }, q));
        Assert.Null(LyricsMatcher.Pick(new[] { Cand("Yoru ni Kakeru", "ヨアソビ", 261.3, "lrclib", rank: 0) }, q));
    }

    [Fact]
    public void Plain_only_candidates_are_ignored()
    {
        var pick = LyricsMatcher.Pick(new[] { Cand("Song", "Artist", 200, synced: "") }, Query("Song", "Artist", 200));
        Assert.Null(pick);
    }

    [Fact]
    public void Instrumental_candidate_is_picked_without_lyrics()
    {
        var pick = LyricsMatcher.Pick(new[] { Cand("Song", "Artist", 200, synced: "", instrumental: true) }, Query("Song", "Artist", 200));
        Assert.True(pick!.Value.best.Instrumental);
    }

    // ───────────── Pick: 배속 버전 / 길이 모름 ─────────────

    [Fact]
    public void Speed_variant_scales_the_original_lyrics()
    {
        var q = Query("Song (Sped Up)", "Artist", 150);
        var original = Cand("Song", "Artist", 200);

        var pick = LyricsMatcher.Pick(new[] { original }, q);
        Assert.Equal(0.75, pick!.Value.scale, 3);

        // 후보를 아직 다 모으지 않은 중간 단계에서는 배속 보정으로 넘어가지 않는다
        Assert.Null(LyricsMatcher.Pick(new[] { original }, q, exactOnly: true));
    }

    [Fact]
    public void Speed_variant_prefers_a_real_match_over_scaling()
    {
        var q = Query("Song (Sped Up)", "Artist", 150);
        var pick = LyricsMatcher.Pick(new[] { Cand("Song", "Artist", 200), Cand("Song (Sped Up)", "Artist", 150.2) }, q);
        Assert.Equal(1.0, pick!.Value.scale);
        Assert.Equal(150.2, pick.Value.best.Duration);
    }

    [Fact]
    public void Normal_track_is_never_scaled()
    {
        Assert.Null(LyricsMatcher.Pick(new[] { Cand("Song", "Artist", 200) }, Query("Song", "Artist", 150)));
    }

    [Fact]
    public void Unknown_duration_falls_back_to_title_and_artist()
    {
        var q = Query("Song", "Artist", 0);
        Assert.NotNull(LyricsMatcher.Pick(new[] { Cand("Song", "Artist", 200) }, q));
        Assert.Null(LyricsMatcher.Pick(new[] { Cand("Song", "Someone Else", 200) }, q));
        Assert.Null(LyricsMatcher.Pick(new[] { Cand("Song", "Artist", 200) }, q, exactOnly: true));
    }

    [Fact]
    public void Short_title_does_not_match_a_longer_unrelated_title()
    {
        // "Die"가 "Die Young"에 들어 있다고 같은 곡으로 보면 안 된다 (길이가 우연히 비슷해도)
        var pick = LyricsMatcher.Pick(new[] { Cand("Die Young", "Other", 201.9) }, Query("Die", "Artist", 200));
        Assert.Null(pick);
    }

    // ───────────── ParseLrc ─────────────

    [Fact]
    public void ParseLrc_reads_timestamps_in_milliseconds()
    {
        var lines = LyricsMatcher.ParseLrc("[00:12.34]first\n[01:02.5]second\n[00:03:50]colon form");
        Assert.Equal(new[] { 3500.0, 12340.0, 62500.0 }, lines.Select(l => l.T));
        Assert.Equal(new[] { "colon form", "first", "second" }, lines.Select(l => l.Text));
    }

    [Fact]
    public void ParseLrc_expands_repeated_timestamps_and_keeps_blank_lines()
    {
        var lines = LyricsMatcher.ParseLrc("[00:10.00][00:30.00]chorus\n[00:20.00]\n[ti:Title]\nno tag here");
        Assert.Equal(3, lines.Count);
        Assert.Equal(new[] { "chorus", "", "chorus" }, lines.Select(l => l.Text));
    }

    [Fact]
    public void ParseLrc_drops_credit_lines()
    {
        var lines = LyricsMatcher.ParseLrc("[00:00.00]作词 : 某人\n[00:01.00]作曲：某人\n[00:02.00]Lyrics: Someone\n[00:05.00]real line");
        Assert.Single(lines);
        Assert.Equal("real line", lines[0].Text);
    }

    [Fact]
    public void ParseLrc_applies_scale()
    {
        var lines = LyricsMatcher.ParseLrc("[01:00.00]a", scale: 0.75);
        Assert.Equal(45000, lines[0].T);
    }

    [Fact]
    public void ParseLrc_keeps_original_order_for_equal_timestamps()
    {
        var lines = LyricsMatcher.ParseLrc("[00:10.00]one\n[00:10.00]two\n[00:10.00]three");
        Assert.Equal(new[] { "one", "two", "three" }, lines.Select(l => l.Text));
    }

    // ───────────── 진단 ─────────────

    [Fact]
    public void Describe_explains_why_a_candidate_was_close()
    {
        var q = Query("Song", "Artist", 200);
        string text = LyricsMatcher.Describe(Cand("Song", "Other", 212.3, "netease", rank: 1), q);
        Assert.Contains("[netease#1]", text);
        Assert.Contains("Δ+12.3s", text);
        Assert.Contains("title=ok", text);
        Assert.Contains("artist=no", text);
    }

    [Fact]
    public void Nearest_lists_title_and_artist_matches_first()
    {
        var q = Query("Song", "Artist", 200);
        var nearest = LyricsMatcher.Nearest(new[]
        {
            Cand("Unrelated", "Nobody", 200.1),
            Cand("Song", "Artist", 230),
            Cand("Song", "Nobody", 205),
        }, q, 2).ToList();
        Assert.Equal(230, nearest[0].Duration);
        Assert.Equal(205, nearest[1].Duration);
    }
}
