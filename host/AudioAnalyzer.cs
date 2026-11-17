using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace SpotifyWallpaper;

// 소리를 캡처해 64개 로그 밴드(0..1)로 만든다.
//  - 화면이 요청한 주기(SetRate)로만 분석한다. 0이면 분석을 멈추고, 그 상태가 이어지면 캡처도 놓는다
//    (가사 없는 곡의 파형이 보일 땐 30Hz, 배경만 숨 쉴 땐 10Hz, 일시정지·가려짐·숨김이면 0)
//  - 우선: Spotify 프로세스 소리만 캡처 (출력 장치·볼륨 경로·다른 앱 소리와 무관)
//  - 대체: Spotify는 켜져 있는데 그 소리만 따로 잡는 데 실패하면 기본 출력 장치 전체(WASAPI 루프백)
//  - Spotify가 꺼져 있으면(SpotifyPresent = false) 아무것도 캡처하지 않고 오디오 세션도 훑지 않음
public sealed class AudioAnalyzer : IDisposable
{
    const int N = 4096, M = 12, Bands = 64;
    const double FMin = 25, FMax = 16000;
    const int SourceCheckMs = 3000;
    const int IdleReleaseMs = 10_000; // 분석이 필요 없어진 뒤 캡처를 놓기까지 (잠깐 멈췄다 재개할 땐 그대로 이어 씀)

    readonly float[] _ring = new float[N];
    readonly Complex[] _fft = new Complex[N];
    readonly float[] _window = new float[N];
    readonly float[] _bands = new float[Bands];
    readonly object _lock = new();
    int _write;
    long _lastDataAt;
    bool _silentSent;

    // 밴드별 FFT 구간과 고역 보정값 (샘플레이트가 바뀔 때만 다시 계산)
    readonly int[] _binFrom = new int[Bands], _binTo = new int[Bands];
    readonly double[] _tiltDb = new double[Bands];
    int _binsForRate;

    // 소스 샘플레이트를 ~48kHz로 낮추는 단순 데시메이션 (192kHz 장치 대비)
    int _sampleRate = 48000;
    int _decim = 1;
    float _decimSum;
    int _decimCount;

    ProcessLoopbackCapture? _spotify;
    int _spotifyPid;
    int _failedPid;   // 캡처에 실패한 Spotify 프로세스 (프로세스가 바뀌면 다시 시도)
    WasapiLoopbackCapture? _system;
    readonly MMDeviceEnumerator _enumerator = new();
    System.Threading.Timer? _computeTimer, _sourceTimer;
    int _checking, _computing;
    int _rate;        // 분석 주기(Hz). 0 = 필요 없음
    long _idleSince;
    volatile bool _spotifyPresent;
    volatile bool _disposed;

    public event Action<float[]>? Frame;

    public AudioAnalyzer()
    {
        for (int i = 0; i < N; i++)
            _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (N - 1))));
        for (int b = 0; b < Bands; b++)
            _tiltDb[b] = 2.5 * Math.Log2(Math.Sqrt(BandEdge(b) * BandEdge(b + 1)) / 1000); // 고역 보정 +2.5dB/oct
    }

    static double BandEdge(int b) => FMin * Math.Pow(FMax / FMin, (double)b / Bands);

    public void Start()
    {
        _idleSince = Environment.TickCount64;
        _computeTimer = new System.Threading.Timer(_ => Compute(), null, Timeout.Infinite, Timeout.Infinite);
        _sourceTimer = new System.Threading.Timer(_ => _ = CheckSourceAsync(), null, SourceCheckMs, SourceCheckMs);
    }

    // 화면이 필요로 하는 분석 주기(Hz). 0이면 분석을 멈춘다.
    public void SetRate(int hz)
    {
        hz = Math.Clamp(hz, 0, 60);
        int old = Interlocked.Exchange(ref _rate, hz);
        if (old == hz || _disposed) return;
        try
        {
            if (hz == 0)
            {
                Interlocked.Exchange(ref _idleSince, Environment.TickCount64);
                _computeTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            else
            {
                _computeTimer?.Change(0, 1000 / hz);
                if (old == 0) CheckSourceSoon(); // 다음 점검(최대 3초)을 기다리지 않고 바로 소스를 잡는다
            }
        }
        catch (ObjectDisposedException) { }
    }

    // Windows 미디어 컨트롤에 Spotify가 보이는지. 없으면 캡처도, 오디오 세션 훑기도 하지 않는다.
    public bool SpotifyPresent
    {
        get => _spotifyPresent;
        set
        {
            if (_spotifyPresent == value) return;
            _spotifyPresent = value;
            if (!_disposed) CheckSourceSoon();
        }
    }

    // 소스 점검은 반드시 스레드 풀에서 돌린다. 호출한 쪽이 UI 스레드(STA)일 수 있는데,
    // 오디오 COM 객체는 스레드 간 전달이 안 돼서 UI 스레드에서 만들면 E_NOINTERFACE로 실패한다.
    void CheckSourceSoon() => _ = Task.Run(CheckSourceAsync);

    // ───────────── 소스 관리 ─────────────

    async Task CheckSourceAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            bool wanted = _spotifyPresent && Volatile.Read(ref _rate) > 0;
            if (!wanted)
            {
                bool capturing = _spotify != null || _system != null;
                bool idleLong = Environment.TickCount64 - Interlocked.Read(ref _idleSince) > IdleReleaseMs;
                if (capturing && (!_spotifyPresent || idleLong))
                {
                    StopSpotify();
                    StopSystem();
                    _failedPid = 0;
                    Log.Write("audio source: released (not needed)");
                }
                return;
            }

            // Spotify 소리를 잘 받고 있으면 오디오 세션 전체를 훑을 필요 없음 (COM 호출이 꽤 무거움)
            if (_spotify != null && ProcessAlive(_spotifyPid)) return;

            int pid = FindSpotifyAudioPid();
            if (pid != 0 && pid != _failedPid && (_spotify == null || pid != _spotifyPid))
            {
                await StartSpotifyAsync(pid);
            }
            else if (pid == 0 && _spotify != null && !ProcessAlive(_spotifyPid))
            {
                StopSpotify();
            }
            // 시스템 전체 소리는 "Spotify는 있는데 Spotify 소리만 따로 잡는 데 실패했을 때"만 대신 사용.
            // Spotify가 없으면 아무것도 캡처하지 않음 (유튜브·게임 소리에 반응하지 않도록)
            bool needSystem = _spotify == null && pid != 0 && pid == _failedPid;
            if (needSystem && _system == null) StartSystem();
            if (!needSystem && _system != null) StopSystem();
        }
        catch (Exception ex) { Log.Write("audio source check: " + ex.Message); }
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    async Task StartSpotifyAsync(int pid)
    {
        StopSpotify();
        try
        {
            var cap = await ProcessLoopbackCapture.StartAsync(pid).ConfigureAwait(false);
            cap.DataAvailable += (buf, frames) => Feed(buf, frames, ProcessLoopbackCapture.Channels, ProcessLoopbackCapture.SampleRate);
            cap.Stopped += ex =>
            {
                // 캡처가 스스로 멈춘 경우(장치 변경 등): 자원을 놓고, 다음 점검 때 다시 잡게 한다
                if (ex == null) return;
                Log.Write("spotify capture stopped: " + ex.Message);
                if (ReferenceEquals(_spotify, cap)) _spotify = null;
                try { cap.Dispose(); } catch { }
            };
            _spotify = cap;
            _spotifyPid = pid;
            _failedPid = 0;
            Log.Write($"audio source: Spotify process (pid {pid})");
        }
        catch (Exception ex)
        {
            Log.Write("spotify capture failed, using system audio: " + ex.Message);
            _failedPid = pid;
        }
    }

    void StopSpotify()
    {
        var cap = _spotify;
        _spotify = null;
        try { cap?.Dispose(); } catch { }
    }

    void StartSystem()
    {
        try
        {
            var cap = new WasapiLoopbackCapture();
            var fmt = cap.WaveFormat;
            cap.DataAvailable += (_, e) =>
            {
                int bytesPerSample = fmt.BitsPerSample / 8;
                int frames = e.BytesRecorded / (bytesPerSample * fmt.Channels);
                FeedBytes(e.Buffer, frames, fmt.Channels, fmt.BitsPerSample == 32, fmt.SampleRate);
            };
            cap.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null) Log.Write("system capture stopped: " + e.Exception.Message);
                if (ReferenceEquals(_system, cap)) _system = null; // 다음 점검 때 다시 시작 (기본 장치 변경 포함)
            };
            cap.StartRecording();
            _system = cap;
            Log.Write($"audio source: system loopback ({fmt})");
        }
        catch (Exception ex) { Log.Write("system capture failed: " + ex.Message); }
    }

    void StopSystem()
    {
        var cap = _system;
        _system = null;
        try { cap?.StopRecording(); cap?.Dispose(); } catch { }
    }

    // Spotify가 실제로 소리를 내는 프로세스(오디오 세션 소유자)를 찾는다
    int FindSpotifyAudioPid()
    {
        try
        {
            foreach (var dev in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                // 장치·세션 객체는 COM 참조라서 쓰고 바로 놓는다 (3초마다 도는 코드라 쌓이면 꽤 됨)
                using (dev)
                {
                    var sessions = dev.AudioSessionManager;
                    sessions.RefreshSessions();
                    int count = sessions.Sessions.Count;
                    for (int i = 0; i < count; i++)
                    {
                        using var session = sessions.Sessions[i];
                        int pid = (int)session.GetProcessID;
                        if (pid != 0 && ProcessName(pid) == "Spotify") return pid;
                    }
                }
            }
        }
        catch (Exception ex) { Log.Write("audio session scan: " + ex.Message); }
        return 0;
    }

    static string ProcessName(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
        catch { return ""; }
    }

    static bool ProcessAlive(int pid) => pid != 0 && ProcessName(pid).Length > 0;

    // ───────────── 샘플 입력 ─────────────

    void SetSourceRate(int rate)
    {
        int decim = Math.Max(1, (int)Math.Round(rate / 48000.0));
        if (decim == _decim && rate / decim == _sampleRate) return;
        _decim = decim;
        _sampleRate = rate / decim;
        _decimSum = 0;
        _decimCount = 0;
    }

    void PushMono(float v)
    {
        _decimSum += v;
        if (++_decimCount < _decim) return;
        _ring[_write] = _decimSum / _decim;
        _write = (_write + 1) % N;
        _decimSum = 0;
        _decimCount = 0;
    }

    void Feed(float[] interleaved, int frames, int channels, int rate)
    {
        lock (_lock)
        {
            SetSourceRate(rate);
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += interleaved[i * channels + c];
                PushMono(sum / channels);
            }
        }
        if (frames > 0) Interlocked.Exchange(ref _lastDataAt, Environment.TickCount64);
    }

    void FeedBytes(byte[] buffer, int frames, int channels, bool isFloat, int rate)
    {
        int bytesPerSample = isFloat ? 4 : 2;
        lock (_lock)
        {
            SetSourceRate(rate);
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int p = (i * channels + c) * bytesPerSample;
                    sum += isFloat ? BitConverter.ToSingle(buffer, p) : BitConverter.ToInt16(buffer, p) / 32768f;
                }
                PushMono(sum / channels);
            }
        }
        if (frames > 0) Interlocked.Exchange(ref _lastDataAt, Environment.TickCount64);
    }

    // ───────────── 분석 ─────────────

    void EnsureBins(int rate)
    {
        if (_binsForRate == rate) return;
        for (int b = 0; b < Bands; b++)
        {
            _binFrom[b] = (int)Math.Floor(BandEdge(b) * N / rate);
            _binTo[b] = Math.Max(_binFrom[b] + 1, (int)Math.Ceiling(BandEdge(b + 1) * N / rate));
        }
        _binsForRate = rate;
    }

    void Compute()
    {
        if (_disposed || Volatile.Read(ref _rate) == 0) return;
        if (Interlocked.Exchange(ref _computing, 1) == 1) return; // 앞 계산이 아직 안 끝났으면 건너뜀
        try
        {
            // 소리가 없으면 데이터가 안 오므로 0을 한 번만 보내고 조용히 대기
            if (Environment.TickCount64 - Interlocked.Read(ref _lastDataAt) > 200)
            {
                if (_silentSent) return;
                _silentSent = true;
                Array.Clear(_bands);
                Frame?.Invoke(_bands);
                return;
            }
            _silentSent = false;

            int rate;
            lock (_lock)
            {
                rate = _sampleRate;
                for (int i = 0; i < N; i++)
                {
                    _fft[i].X = _ring[(_write + i) % N] * _window[i];
                    _fft[i].Y = 0;
                }
            }
            FastFourierTransform.FFT(true, M, _fft);
            EnsureBins(rate);

            int half = N / 2;
            for (int b = 0; b < Bands; b++)
            {
                double best = 0;
                for (int k = _binFrom[b]; k < _binTo[b] && k < half; k++)
                    best = Math.Max(best, _fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);
                double db = 10 * Math.Log10(best + 1e-18) + _tiltDb[b];
                _bands[b] = (float)Math.Clamp((db + 100) / 65, 0, 1);
            }
            Frame?.Invoke(_bands);
        }
        catch (Exception ex) { Log.Write("audio compute: " + ex.Message); }
        finally { Interlocked.Exchange(ref _computing, 0); }
    }

    public void Dispose()
    {
        _disposed = true;
        _computeTimer?.Dispose();
        _sourceTimer?.Dispose();
        StopSpotify();
        StopSystem();
        _enumerator.Dispose();
    }
}
