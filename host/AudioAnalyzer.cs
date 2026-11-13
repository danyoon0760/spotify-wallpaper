using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace SpotifyWallpaper;

// 소리를 캡처해 64개 로그 밴드(0..1)로 만든다 (~60Hz).
//  - 우선: Spotify 프로세스 소리만 캡처 (출력 장치·볼륨 경로·다른 앱 소리와 무관)
//  - 대체: Spotify는 켜져 있는데 그 소리만 따로 잡는 데 실패하면 기본 출력 장치 전체(WASAPI 루프백)
//  - Spotify가 꺼져 있으면 아무것도 캡처하지 않음
// 3초마다 소스를 점검해 Spotify가 켜지거나 꺼지면 자동으로 전환한다.
public sealed class AudioAnalyzer : IDisposable
{
    const int N = 4096, M = 12, Bands = 64;
    const double FMin = 25, FMax = 16000;

    readonly float[] _ring = new float[N];
    readonly Complex[] _fft = new Complex[N];
    readonly float[] _window = new float[N];
    readonly float[] _bands = new float[Bands];
    readonly object _lock = new();
    int _write;
    long _lastDataAt;
    bool _silentSent;

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
    int _checking;
    volatile bool _disposed;

    public bool Paused { get; set; }
    public event Action<float[]>? Frame;

    public AudioAnalyzer()
    {
        for (int i = 0; i < N; i++)
            _window[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (N - 1))));
    }

    public void Start()
    {
        // 30Hz면 충분 (화면 쪽에서 엔벨로프로 부드럽게 보간함)
        _computeTimer = new System.Threading.Timer(_ => Compute(), null, 0, 33);
        _sourceTimer = new System.Threading.Timer(_ => _ = CheckSourceAsync(), null, 0, 3000);
    }

    // ───────────── 소스 관리 ─────────────

    async Task CheckSourceAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
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
            var cap = await ProcessLoopbackCapture.StartAsync(pid);
            cap.DataAvailable += (buf, frames) => Feed(buf, frames, ProcessLoopbackCapture.Channels, ProcessLoopbackCapture.SampleRate);
            cap.Stopped += ex =>
            {
                if (ex != null) Log.Write("spotify capture stopped: " + ex.Message);
                _spotify = null;
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
                var sessions = dev.AudioSessionManager;
                sessions.RefreshSessions();
                for (int i = 0; i < sessions.Sessions.Count; i++)
                {
                    int pid = (int)sessions.Sessions[i].GetProcessID;
                    if (pid != 0 && ProcessName(pid) == "Spotify") return pid;
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
        if (frames > 0) _lastDataAt = Environment.TickCount64;
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
        if (frames > 0) _lastDataAt = Environment.TickCount64;
    }

    // ───────────── 분석 ─────────────

    void Compute()
    {
        if (Paused || _disposed) return;

        // 소리가 없으면 데이터가 안 오므로 0을 한 번만 보내고 조용히 대기
        if (Environment.TickCount64 - _lastDataAt > 200)
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

        int half = N / 2;
        for (int b = 0; b < Bands; b++)
        {
            double f0 = FMin * Math.Pow(FMax / FMin, (double)b / Bands);
            double f1 = FMin * Math.Pow(FMax / FMin, (double)(b + 1) / Bands);
            int k0 = (int)Math.Floor(f0 * N / rate);
            int k1 = Math.Max(k0 + 1, (int)Math.Ceiling(f1 * N / rate));
            double best = 0;
            for (int k = k0; k < k1 && k < half; k++)
                best = Math.Max(best, Math.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y));
            double db = 20 * Math.Log10(best + 1e-9) + 2.5 * Math.Log2(Math.Sqrt(f0 * f1) / 1000); // 고역 보정 +2.5dB/oct
            _bands[b] = (float)Math.Clamp((db + 100) / 65, 0, 1);
        }
        Frame?.Invoke(_bands);
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
