using System.Diagnostics;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpotifyWallpaper;

public sealed record TrackInfo(string Key, string Title, string Artist, string Album, byte[]? Thumbnail, string ThumbHash);

// Windows 미디어 컨트롤(SMTC)에서 Spotify 재생 정보만 읽는다. Spotify가 없으면 "정지" 상태.
public sealed class MediaWatcher : IDisposable
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;
    GlobalSystemMediaTransportControlsSession? _session;
    string? _sessionId;
    CancellationTokenSource? _debounce;
    System.Threading.Timer? _tick;
    readonly object _lock = new();

    TrackInfo? _current;
    string _playback = "stopped";

    // ── 재생 위치 시계 ──
    // Spotify는 위치 값(Position)을 그대로 둔 채 LastUpdatedTime만 몇 초마다 갱신하므로
    // "Position + (지금 - LastUpdatedTime)"을 그대로 믿으면 위치가 앞뒤로 튄다.
    // 그래서 Position 값이 실제로 바뀔 때만 기준점을 다시 잡고, 그 사이는 자체 스톱워치로 진행시킨다.
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly object _clockLock = new();
    double _anchorPos;
    long _anchorMs;
    double _lastRawPos = double.NaN;
    double _duration;

    public event Action<TrackInfo?>? TrackChanged;
    public event Action<string>? PlaybackChanged;
    public event Action<double, double>? TimelineChanged; // position ms, duration ms
    public event Action<bool>? SessionPresentChanged;     // Spotify가 미디어 컨트롤에 나타났는지/사라졌는지

    public TrackInfo? Current => _current;
    public string Playback => _playback;
    // Spotify가 켜져 있어 미디어 컨트롤에 세션이 있는지
    public bool SessionPresent => _session != null;
    // 곡이 바뀌기 직전 곡의 길이 (새 곡 길이가 갱신됐는지 확인용)
    public double PreviousDuration { get; private set; }

    public async Task StartAsync()
    {
        _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mgr.SessionsChanged += (_, _) => PickSession();
        _mgr.CurrentSessionChanged += (_, _) => PickSession();
        PickSession();
        // 화면 쪽은 받은 위치에서 스스로 시간을 흘려 보내므로 1초 간격 보정이면 충분
        _tick = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    void Tick()
    {
        try { SyncTimeline(); EmitTimeline(); }
        catch (Exception ex) { Log.Write("media tick: " + ex.Message); }
    }

    // 시스템 콜백(WinRT 이벤트)에서 불리므로 예외가 밖으로 나가면 앱이 죽는다 — 여기서 잡아 기록만 한다
    void PickSession()
    {
        try { PickSessionCore(); }
        catch (Exception ex) { Log.Write("media session pick: " + ex.Message); }
    }

    void PickSessionCore()
    {
        if (_mgr == null) return;
        lock (_lock)
        {
            // Spotify만 따라감 (브라우저 유튜브 등 다른 앱 미디어는 무시)
            var pick = _mgr.GetSessions().FirstOrDefault(s =>
                s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
            string? id = pick?.SourceAppUserModelId;
            if (id == _sessionId && _session != null) return;

            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnProps;
                _session.PlaybackInfoChanged -= OnPlayback;
                _session.TimelinePropertiesChanged -= OnTimeline;
            }
            _session = pick;
            _sessionId = id;
            lock (_clockLock) _lastRawPos = double.NaN;
            SessionPresentChanged?.Invoke(_session != null);

            if (_session == null)
            {
                _current = null;
                _playback = "stopped";
                TrackChanged?.Invoke(null);
                PlaybackChanged?.Invoke("stopped");
                return;
            }
            _session.MediaPropertiesChanged += OnProps;
            _session.PlaybackInfoChanged += OnPlayback;
            _session.TimelinePropertiesChanged += OnTimeline;
        }
        ScheduleRefresh(0);
        EmitPlayback();
    }

    void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => ScheduleRefresh(250);

    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
    {
        try { EmitPlayback(); SyncTimeline(); EmitTimeline(); }
        catch (Exception ex) { Log.Write("media playback event: " + ex.Message); }
    }

    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e)
    {
        try { SyncTimeline(); EmitTimeline(); }
        catch (Exception ex) { Log.Write("media timeline event: " + ex.Message); }
    }

    // Spotify는 곡이 바뀔 때 속성 이벤트를 여러 번 보내므로 잠깐 모았다가 읽는다
    void ScheduleRefresh(int delayMs)
    {
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token);
                await RefreshAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Write("media refresh: " + ex.Message); }
        });
    }

    // 곡이 바뀐 뒤 Spotify가 썸네일을 늦게 바꾸면서 이벤트를 안 보내는 경우가 있어, 몇 번 더 직접 읽어 본다
    static readonly int[] RecheckDelaysMs = { 600, 900, 1500, 3000 }; // 누적 0.6s, 1.5s, 3s, 6s
    CancellationTokenSource? _recheck;
    readonly SemaphoreSlim _refreshGate = new(1, 1);

    void ScheduleRechecks()
    {
        _recheck?.Cancel();
        var cts = _recheck = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (int delay in RecheckDelaysMs)
                {
                    await Task.Delay(delay, cts.Token);
                    await RefreshAsync(cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Write("media recheck: " + ex.Message); }
        });
    }

    async Task RefreshAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try { await RefreshCoreAsync(ct); }
        finally { _refreshGate.Release(); }
    }

    async Task RefreshCoreAsync(CancellationToken ct)
    {
        var s = _session;
        if (s == null) return;
        var props = await s.TryGetMediaPropertiesAsync();
        if (ct.IsCancellationRequested || props == null || string.IsNullOrWhiteSpace(props.Title)) return;

        byte[]? thumb = null;
        if (props.Thumbnail != null)
        {
            try
            {
                using var stream = await props.Thumbnail.OpenReadAsync();
                if (stream.Size > 0 && stream.Size < 20_000_000)
                {
                    using var reader = new DataReader(stream);
                    await reader.LoadAsync((uint)stream.Size);
                    thumb = new byte[stream.Size];
                    reader.ReadBytes(thumb);
                }
            }
            catch (Exception ex) { Log.Write("thumbnail: " + ex.Message); }
        }
        if (ct.IsCancellationRequested) return;

        string artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist : props.Artist;
        string key = $"{props.Title}|{artist}|{props.AlbumTitle}";
        string hash = thumb == null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(thumb));

        if (_current != null && _current.Key == key && _current.ThumbHash == hash) return;
        bool newTrack = _current?.Key != key;
        if (newTrack)
        {
            // 새 곡: 시계를 SMTC 값(없으면 0)으로 다시 맞춤
            lock (_clockLock)
            {
                PreviousDuration = _duration;
                _lastRawPos = double.NaN;
                _anchorPos = 0;
                _anchorMs = _clock.ElapsedMilliseconds;
            }
            SyncTimeline();
        }
        _current = new TrackInfo(key, props.Title, artist ?? "", props.AlbumTitle ?? "", thumb, hash);
        TrackChanged?.Invoke(_current);
        EmitTimeline();
        if (newTrack) ScheduleRechecks();
    }

    void EmitPlayback()
    {
        var s = _session;
        if (s == null) return;
        string state;
        try
        {
            state = s.GetPlaybackInfo().PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
                _ => "stopped",
            };
        }
        catch { return; }

        if (state == _playback) return;
        lock (_clockLock)
        {
            // 상태가 바뀌는 순간의 위치로 기준점 고정 후 전환
            _anchorPos = EstimateLocked();
            _anchorMs = _clock.ElapsedMilliseconds;
            _playback = state;
        }
        PlaybackChanged?.Invoke(state);
    }

    double EstimateLocked() =>
        _anchorPos + (_playback == "playing" ? _clock.ElapsedMilliseconds - _anchorMs : 0);

    void SyncTimeline()
    {
        var s = _session;
        if (s == null) return;
        GlobalSystemMediaTransportControlsSessionTimelineProperties tl;
        try { tl = s.GetTimelineProperties(); }
        catch { return; }

        double dur = (tl.EndTime - tl.StartTime).TotalMilliseconds;
        double raw = tl.Position.TotalMilliseconds;
        lock (_clockLock)
        {
            _duration = dur > 0 ? dur : 0;
            if (dur <= 0) return; // 타임라인 미지원 앱: 자체 시계만 사용

            if (double.IsNaN(_lastRawPos) || Math.Abs(raw - _lastRawPos) > 1)
            {
                // 위치 값이 실제로 바뀌었을 때만 LastUpdatedTime을 믿는다 (너무 오래된 값은 무시)
                double age = (DateTimeOffset.Now - tl.LastUpdatedTime).TotalMilliseconds;
                double pos = raw + (_playback == "playing" && age >= 0 && age < 3000 ? age : 0);
                _anchorPos = pos;
                _anchorMs = _clock.ElapsedMilliseconds;
                _lastRawPos = raw;
            }
        }
    }

    public (double pos, double dur) GetTimeline()
    {
        lock (_clockLock)
        {
            double pos = Math.Max(0, EstimateLocked());
            if (_duration > 0) pos = Math.Min(pos, _duration);
            return (pos, _duration);
        }
    }

    // 재생 중엔 주기적으로(화면 쪽 시계 보정용), 정지 중엔 값이 바뀔 때만 보냄
    double _emittedPos = double.NaN, _emittedDur = double.NaN;

    void EmitTimeline()
    {
        if (_session == null) return;
        var (pos, dur) = GetTimeline();
        if (_playback != "playing" && Math.Abs(pos - _emittedPos) < 1 && dur == _emittedDur) return;
        _emittedPos = pos;
        _emittedDur = dur;
        TimelineChanged?.Invoke(pos, dur);
    }

    public void Dispose()
    {
        _tick?.Dispose();
        _debounce?.Cancel();
        _recheck?.Cancel();
    }
}
