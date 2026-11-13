using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace SpotifyWallpaper;

// 특정 프로세스(와 자식 프로세스)가 내는 소리만 캡처 (Windows 10 2004+ "process loopback").
// 출력 장치·마스터 볼륨·다른 앱 소리와 무관하게 Spotify 소리만 받을 수 있다.
internal sealed class ProcessLoopbackCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    readonly AudioClient _client;
    readonly AutoResetEvent _event = new(false);
    readonly Thread _thread;
    volatile bool _stop;

    // 인터리브드 float 샘플, 프레임 수
    public event Action<float[], int>? DataAvailable;
    public event Action<Exception?>? Stopped;

    ProcessLoopbackCapture(AudioClient client)
    {
        _client = client;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "ProcessLoopback" };
    }

    public static async Task<ProcessLoopbackCapture> StartAsync(int processId)
    {
        var client = await ActivateAsync(processId);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
            2_000_000, 0, format, Guid.Empty); // 200ms 버퍼
        var capture = new ProcessLoopbackCapture(client);
        client.SetEventHandle(capture._event.SafeWaitHandle.DangerousGetHandle());
        client.Start();
        capture._thread.Start();
        return capture;
    }

    void CaptureLoop()
    {
        Exception? error = null;
        var buffer = new float[SampleRate * Channels]; // 1초 분량이면 충분
        try
        {
            var cc = _client.AudioCaptureClient;
            while (!_stop)
            {
                _event.WaitOne(100);
                int packet;
                while (!_stop && (packet = cc.GetNextPacketSize()) > 0)
                {
                    IntPtr data = cc.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    int count = Math.Min(frames * Channels, buffer.Length);
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(buffer, 0, count);
                    else Marshal.Copy(data, buffer, 0, count);
                    cc.ReleaseBuffer(frames);
                    DataAvailable?.Invoke(buffer, count / Channels);
                }
            }
        }
        catch (Exception ex) { error = ex; }
        try { _client.Stop(); } catch { }
        Stopped?.Invoke(_stop ? null : error);
    }

    public void Dispose()
    {
        _stop = true;
        _event.Set();
        if (Thread.CurrentThread != _thread) _thread.Join(500);
        _client.Dispose();
        _event.Dispose();
    }

    // ───────────── ActivateAudioInterfaceAsync 연동 ─────────────

    const string VirtualProcessLoopback = "VAD\\Process_Loopback";
    static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    static async Task<AudioClient> ActivateAsync(int processId)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, { pid, INCLUDE_TARGET_PROCESS_TREE } }
        var activation = new ActivationParams { ActivationType = 1, TargetProcessId = (uint)processId, ProcessLoopbackMode = 0 };
        IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        IntPtr propPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activation, paramsPtr, false);
            Marshal.StructureToPtr(new PropVariantBlob
            {
                vt = 65, // VT_BLOB
                cbSize = (uint)Marshal.SizeOf<ActivationParams>(),
                pBlobData = paramsPtr,
            }, propPtr, false);

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualProcessLoopback, IID_IAudioClient, propPtr, handler, out _);
            var audioClient = (IAudioClient)await handler.Task;
            return new AudioClient(audioClient);
        }
        finally
        {
            Marshal.FreeHGlobal(propPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    // 완료 콜백은 임의 스레드에서 오므로 agile 객체여야 한다
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        readonly TaskCompletionSource<object> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<object> Task => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out int hr, out object iface);
                if (hr < 0) _tcs.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("activate failed", hr));
                else _tcs.TrySetResult(iface);
            }
            catch (Exception ex) { _tcs.TrySetException(ex); }
        }
    }
}
