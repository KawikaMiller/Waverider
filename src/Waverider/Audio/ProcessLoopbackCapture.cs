using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Waverider.Audio;

// Per-process WASAPI loopback capture using ActivateAudioInterfaceAsync with
// AUDIOCLIENT_ACTIVATION_PARAMS{ ProcessLoopback }. Requires Windows 10 build
// 20348 / Windows 11. NAudio doesn't expose this path, so we go straight to
// the COM activation API and drive a minimal IAudioClient ourselves.
//
// Threading model: every COM call (activation, IAudioClient.Initialize,
// IAudioCaptureClient.GetBuffer, Release, etc.) happens on a single dedicated
// MTA worker thread. This avoids cross-apartment QI failures we hit when the
// activation completion fired on MTA but the result was awaited on STA.
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const string VirtualLoopbackDevice = "VAD\\Process_Loopback";
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const long REFTIMES_PER_SEC = 10_000_000;
    private const int AudioClientActivationTypeProcessLoopback = 1;
    private const int IncludeTargetProcessTree = 0;
    private const int ExcludeTargetProcessTree = 1;
    private const ushort VT_BLOB = 65;

    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    public WaveFormat WaveFormat { get; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private Thread? _worker;
    private CancellationTokenSource? _cts;
    private EventWaitHandle? _bufferReady;
    private Exception? _stopException;

    public ProcessLoopbackCapture()
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    }

    public void Start(uint targetPid, bool includeProcessTree = true)
    {
        if (_worker is not null) throw new InvalidOperationException("Already capturing");

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Exception? initError = null;
        using var initDone = new ManualResetEventSlim(false);

        _worker = new Thread(() =>
        {
            try
            {
                ActivateAndInitialize(targetPid, includeProcessTree);
            }
            catch (Exception ex)
            {
                initError = ex;
                initDone.Set();
                return;
            }
            initDone.Set();

            try { CaptureLoop(token); }
            catch (Exception ex) { _stopException = ex; }
            finally { CleanupComOnWorker(); }
        })
        {
            IsBackground = true,
            Name = "ProcessLoopbackCapture"
        };
        // Activation completion ships the IUnknown back from a thread-pool MTA
        // thread; running our consumer on STA caused QI(IAudioClient) to return
        // E_NOINTERFACE. Pin everything to one MTA thread to keep apartments aligned.
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();

        if (!initDone.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("ProcessLoopback activation timed out");
        if (initError is not null) throw initError;
    }

    public void Stop()
    {
        if (_worker is null) return;
        _cts?.Cancel();
        _bufferReady?.Set();
        _worker.Join(2000);
        _worker = null;
        _cts?.Dispose();
        _cts = null;
        _bufferReady?.Dispose();
        _bufferReady = null;
        RecordingStopped?.Invoke(this, new StoppedEventArgs(_stopException));
        _stopException = null;
    }

    public void Dispose() => Stop();

    private unsafe void ActivateAndInitialize(uint targetPid, bool includeProcessTree)
    {
        const int ActivationParamsSize = 12;
        const int PropVariantSize = 24;

        IntPtr pActParams = Marshal.AllocHGlobal(ActivationParamsSize);
        IntPtr pProp = Marshal.AllocHGlobal(PropVariantSize);
        IntPtr pFormat = WaveFormatToHGlobal(WaveFormat);

        new Span<byte>((void*)pActParams, ActivationParamsSize).Clear();
        new Span<byte>((void*)pProp, PropVariantSize).Clear();

        byte* pa = (byte*)pActParams;
        *(int*)(pa + 0) = AudioClientActivationTypeProcessLoopback;
        *(uint*)(pa + 4) = targetPid;
        *(int*)(pa + 8) = includeProcessTree ? IncludeTargetProcessTree : ExcludeTargetProcessTree;

        byte* pp = (byte*)pProp;
        *(ushort*)(pp + 0) = VT_BLOB;
        *(uint*)(pp + 8) = ActivationParamsSize;
        *(IntPtr*)(pp + 16) = pActParams;

        Debug.WriteLine($"[ProcessLoopback] Activating PID={targetPid} includeTree={includeProcessTree}");

        IActivateAudioInterfaceAsyncOperation? asyncOp = null;
        IntPtr unkPtr = IntPtr.Zero;
        IntPtr pAudioClient = IntPtr.Zero;
        try
        {
            var handler = new ActivateCompletionHandler();
            int hr = NativeMethods.ActivateAudioInterfaceAsync(
                VirtualLoopbackDevice, IID_IAudioClient, pProp, handler, out asyncOp);
            Debug.WriteLine($"[ProcessLoopback] ActivateAudioInterfaceAsync hr=0x{hr:X8}");
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            unkPtr = handler.Task.GetAwaiter().GetResult();
            Debug.WriteLine($"[ProcessLoopback] activation returned IUnknown=0x{unkPtr.ToInt64():X}");

            Guid iid = IID_IAudioClient;
            int qiHr = Marshal.QueryInterface(unkPtr, ref iid, out pAudioClient);
            Debug.WriteLine($"[ProcessLoopback] QI(IAudioClient) hr=0x{qiHr:X8} ptr=0x{pAudioClient.ToInt64():X}");
            if (qiHr < 0 || pAudioClient == IntPtr.Zero)
                throw new InvalidOperationException($"QI for IAudioClient failed: 0x{qiHr:X8}");

            _audioClient = (IAudioClient)Marshal.GetTypedObjectForIUnknown(pAudioClient, typeof(IAudioClient));

            int initHr = _audioClient.Initialize(
                shareMode: 0,
                streamFlags: AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                hnsBufferDuration: 2 * REFTIMES_PER_SEC / 10,
                hnsPeriodicity: 0,
                pFormat: pFormat,
                audioSessionGuid: IntPtr.Zero);
            Debug.WriteLine($"[ProcessLoopback] IAudioClient.Initialize hr=0x{initHr:X8}");
            if (initHr < 0) Marshal.ThrowExceptionForHR(initHr);

            _bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset);
            int evtHr = _audioClient.SetEventHandle(_bufferReady.SafeWaitHandle.DangerousGetHandle());
            if (evtHr < 0) Marshal.ThrowExceptionForHR(evtHr);

            var captureIid = IID_IAudioCaptureClient;
            int svcHr = _audioClient.GetService(ref captureIid, out object? captureObj);
            Debug.WriteLine($"[ProcessLoopback] GetService(IAudioCaptureClient) hr=0x{svcHr:X8}");
            if (svcHr < 0 || captureObj is null) Marshal.ThrowExceptionForHR(svcHr);
            _captureClient = (IAudioCaptureClient)captureObj!;

            int startHr = _audioClient.Start();
            if (startHr < 0) Marshal.ThrowExceptionForHR(startHr);
            Debug.WriteLine("[ProcessLoopback] capture started");
        }
        finally
        {
            if (pAudioClient != IntPtr.Zero) Marshal.Release(pAudioClient);
            if (unkPtr != IntPtr.Zero) Marshal.Release(unkPtr);
            if (asyncOp is not null) Marshal.ReleaseComObject(asyncOp);
            Marshal.FreeHGlobal(pFormat);
            Marshal.FreeHGlobal(pProp);
            Marshal.FreeHGlobal(pActParams);
        }
    }

    private void CleanupComOnWorker()
    {
        try { _audioClient?.Stop(); } catch { /* ignore */ }
        if (_captureClient is not null) { Marshal.ReleaseComObject(_captureClient); _captureClient = null; }
        if (_audioClient is not null) { Marshal.ReleaseComObject(_audioClient); _audioClient = null; }
    }

    private void CaptureLoop(CancellationToken token)
    {
        int blockAlign = WaveFormat.BlockAlign;
        while (!token.IsCancellationRequested)
        {
            if (!_bufferReady!.WaitOne(200)) continue;
            if (token.IsCancellationRequested) break;

            while (true)
            {
                int peekHr = _captureClient!.GetNextPacketSize(out uint frames);
                if (peekHr < 0 || frames == 0) break;

                int hr = _captureClient.GetBuffer(out IntPtr pData, out uint numFrames, out uint flags, out _, out _);
                if (hr < 0)
                {
                    try { _captureClient.ReleaseBuffer(0); } catch { }
                    break;
                }

                int bytes = (int)numFrames * blockAlign;
                if (bytes > 0)
                {
                    var managed = new byte[bytes];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                        Marshal.Copy(pData, managed, 0, bytes);
                    DataAvailable?.Invoke(this, new WaveInEventArgs(managed, bytes));
                }
                _captureClient.ReleaseBuffer(numFrames);
            }
        }
    }

    private static IntPtr WaveFormatToHGlobal(WaveFormat fmt)
    {
        const int Size = 18; // WAVEFORMATEX, cbSize=0
        IntPtr p = Marshal.AllocHGlobal(Size);
        short formatTag = fmt.Encoding == WaveFormatEncoding.IeeeFloat ? (short)3 : (short)1;
        Marshal.WriteInt16(p, 0, formatTag);
        Marshal.WriteInt16(p, 2, (short)fmt.Channels);
        Marshal.WriteInt32(p, 4, fmt.SampleRate);
        Marshal.WriteInt32(p, 8, fmt.AverageBytesPerSecond);
        Marshal.WriteInt16(p, 12, (short)fmt.BlockAlign);
        Marshal.WriteInt16(p, 14, (short)fmt.BitsPerSample);
        Marshal.WriteInt16(p, 16, 0);
        return p;
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrameCount);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint numFramesToRead, out uint dwFlags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, out IntPtr activatedInterface);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    [Guid("8DCDF94B-32EC-4F1F-9F6A-AE51A06A6F6F")]
    private class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<IntPtr> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IntPtr> Task => _tcs.Task;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                int hr = activateOperation.GetActivateResult(out int activateResult, out IntPtr unkPtr);
                Debug.WriteLine($"[ProcessLoopback] GetActivateResult: callHr=0x{hr:X8} activateHr=0x{activateResult:X8} ptr=0x{unkPtr.ToInt64():X}");
                if (hr < 0)
                {
                    if (unkPtr != IntPtr.Zero) Marshal.Release(unkPtr);
                    _tcs.TrySetException(Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"GetActivateResult: 0x{hr:X8}"));
                }
                else if (activateResult != 0)
                {
                    if (unkPtr != IntPtr.Zero) Marshal.Release(unkPtr);
                    _tcs.TrySetException(Marshal.GetExceptionForHR(activateResult) ?? new InvalidOperationException($"Activation failed: 0x{activateResult:X8}"));
                }
                else if (unkPtr == IntPtr.Zero)
                {
                    _tcs.TrySetException(new InvalidOperationException("Activation returned null IUnknown"));
                }
                else
                {
                    _tcs.TrySetResult(unkPtr);
                }
            }
            catch (Exception ex) { _tcs.TrySetException(ex); }
            return 0;
        }
    }

    private static class NativeMethods
    {
        [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
        public static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceCompletionHandler completionHandler,
            [MarshalAs(UnmanagedType.Interface)] out IActivateAudioInterfaceAsyncOperation activationOperation);
    }
}
