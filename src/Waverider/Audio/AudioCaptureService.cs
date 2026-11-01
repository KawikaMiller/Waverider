using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Waverider.Audio;

public sealed class AudioCaptureService : IDisposable
{
    private readonly FftAnalyzer _fft;
    private readonly object _lock = new();

    private WasapiLoopbackCapture? _wasapi;
    private ProcessLoopbackCapture? _proc;
    private MMDevice? _device;
    private System.Threading.Timer? _frameTimer;
    private System.Threading.Timer? _watchdog;
    private uint? _currentSpotifyPid;

    private readonly int _windowSamples;
    private readonly float[] _ring;
    private int _ringWrite;
    private int _channels = 2;
    private int _sampleRate = 48000;
    private int _bitsPerSample = 32;
    private bool _isFloat = true;

    public AudioCaptureMode Mode { get; private set; } = AudioCaptureMode.ThisApp;

    /// <summary>PID of the process that owns the built-in player's audio (WebView2's browser process).</summary>
    public Func<uint?>? ThisAppPidProvider { get; set; }
    public string StatusText { get; private set; } = "";
    public bool IsRunning { get; private set; }

    public event Action<AudioFrame>? FrameReady;
    public event Action<bool>? RunningChanged;
    public event Action<string>? StatusChanged;

    public AudioCaptureService(FftAnalyzer fft)
    {
        _fft = fft;
        _windowSamples = fft.FftSize;
        _ring = new float[_windowSamples * 2];
    }

    public void SetMode(AudioCaptureMode mode)
    {
        lock (_lock)
        {
            if (Mode == mode) return;
            Mode = mode;
        }
        Stop();
        Start();
    }

    public void Start()
    {
        lock (_lock)
        {
            if (IsRunning || _watchdog is not null) return;

            if (Mode == AudioCaptureMode.ThisApp)
            {
                // WebView2's processes are not children of this process (its browser process is
                // launched by a system service), so target that browser process directly; the
                // audio service is a descendant of it. The watchdog retries until it's up.
                var pid = ThisAppPidProvider?.Invoke();
                if (pid is null)
                {
                    SetStatus("Waiting for the built-in player to start…");
                }
                else
                {
                    try
                    {
                        StartProcessLoopback(pid.Value);
                        SetStatus("Capturing this app's built-in player.");
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Process loopback failed ({ex.Message}). Falling back to all system audio.");
                        try { StartSystemLoopback(); } catch (Exception ex2) { SetStatus($"System loopback failed: {ex2.Message}"); return; }
                    }
                }
            }
            else if (Mode == AudioCaptureMode.SpotifyOnly)
            {
                var pid = SpotifyProcessLocator.FindMainSpotifyPid();
                if (pid is null)
                {
                    SetStatus("Spotify isn't running — capture will start automatically when it launches.");
                }
                else
                {
                    try
                    {
                        StartProcessLoopback(pid.Value);
                        SetStatus($"Capturing only Spotify (PID {pid}).");
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Process loopback failed ({ex.Message}). Falling back to all system audio.");
                        try { StartSystemLoopback(); } catch (Exception ex2) { SetStatus($"System loopback failed: {ex2.Message}"); return; }
                    }
                }
            }
            else
            {
                try { StartSystemLoopback(); SetStatus("Capturing all system audio."); }
                catch (Exception ex) { SetStatus($"System loopback failed: {ex.Message}"); return; }
            }

            // Watchdog re-checks the Spotify process every few seconds in SpotifyOnly mode.
            _watchdog = new System.Threading.Timer(_ => CheckSpotifyState(), null, 3000, 3000);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _watchdog?.Dispose(); _watchdog = null;
            StopEngine_NoLock();
        }
    }

    private void StopEngine_NoLock()
    {
        _frameTimer?.Dispose(); _frameTimer = null;

        if (_wasapi is not null)
        {
            _wasapi.DataAvailable -= OnDataAvailable;
            _wasapi.RecordingStopped -= OnRecordingStopped;
            try { _wasapi.StopRecording(); } catch { }
            try { _wasapi.Dispose(); } catch { }
            _wasapi = null;
        }
        if (_proc is not null)
        {
            _proc.DataAvailable -= OnDataAvailable;
            _proc.RecordingStopped -= OnRecordingStopped;
            try { _proc.Stop(); } catch { }
            try { _proc.Dispose(); } catch { }
            _proc = null;
        }
        if (_device is not null)
        {
            try { _device.Dispose(); } catch { }
            _device = null;
        }
        _currentSpotifyPid = null;

        if (IsRunning)
        {
            IsRunning = false;
            RunningChanged?.Invoke(false);
        }
    }

    private void StartProcessLoopback(uint pid)
    {
        _proc = new ProcessLoopbackCapture();
        ApplyFormat(_proc.WaveFormat);
        _proc.DataAvailable += OnDataAvailable;
        _proc.RecordingStopped += OnRecordingStopped;
        _proc.Start(pid, includeProcessTree: true);
        _currentSpotifyPid = pid;
        BeginEmitting();
    }

    private void StartSystemLoopback()
    {
        var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        enumerator.Dispose();

        _wasapi = new WasapiLoopbackCapture(_device);
        ApplyFormat(_wasapi.WaveFormat);
        _wasapi.DataAvailable += OnDataAvailable;
        _wasapi.RecordingStopped += OnRecordingStopped;
        _wasapi.StartRecording();
        BeginEmitting();
    }

    private void ApplyFormat(WaveFormat fmt)
    {
        _channels = fmt.Channels;
        _sampleRate = fmt.SampleRate;
        _bitsPerSample = fmt.BitsPerSample;
        _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
    }

    private void BeginEmitting()
    {
        _frameTimer?.Dispose();
        _frameTimer = new System.Threading.Timer(_ => EmitFrame(), null, 0, 16);
        IsRunning = true;
        RunningChanged?.Invoke(true);
    }

    private void CheckSpotifyState()
    {
        if (Mode == AudioCaptureMode.SystemLoopback) return;
        uint? pid;
        try
        {
            pid = Mode == AudioCaptureMode.ThisApp
                ? ThisAppPidProvider?.Invoke()
                : SpotifyProcessLocator.FindMainSpotifyPid();
        }
        catch { return; }

        lock (_lock)
        {
            if (Mode == AudioCaptureMode.SystemLoopback) return;
            if (_wasapi is not null) return; // running on the system-loopback fallback; don't double up

            if (pid is null && _currentSpotifyPid is not null)
            {
                StopEngine_NoLock();
                SetStatus(Mode == AudioCaptureMode.ThisApp
                    ? "Built-in player stopped — waiting for it to restart."
                    : "Spotify closed — waiting for it to reopen.");
                return;
            }

            if (pid is not null && _currentSpotifyPid is null)
            {
                try
                {
                    StartProcessLoopback(pid.Value);
                    SetStatus($"Capturing {(Mode == AudioCaptureMode.ThisApp ? "this app's built-in player" : "only Spotify")} (PID {pid}).");
                }
                catch (Exception ex)
                {
                    SetStatus($"Process loopback failed: {ex.Message}");
                }
                return;
            }

            if (pid is not null && _currentSpotifyPid != pid)
            {
                // Spotify restarted with a different PID — re-arm.
                StopEngine_NoLock();
                try
                {
                    StartProcessLoopback(pid.Value);
                    SetStatus($"Capturing only Spotify (PID {pid}).");
                }
                catch (Exception ex)
                {
                    SetStatus($"Process loopback failed: {ex.Message}");
                }
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int channels = _channels;
        int bytesPerSample = _bitsPerSample / 8;
        int frameBytes = bytesPerSample * channels;
        if (frameBytes == 0) return;
        int frameCount = e.BytesRecorded / frameBytes;
        if (frameCount == 0) return;

        ReadOnlySpan<byte> src = e.Buffer.AsSpan(0, e.BytesRecorded);
        for (int i = 0; i < frameCount; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int offset = i * frameBytes + c * bytesPerSample;
                sum += ReadSample(src, offset);
            }
            float mono = sum / channels;
            _ring[_ringWrite] = mono;
            _ringWrite = (_ringWrite + 1) % _ring.Length;
        }
    }

    private float ReadSample(ReadOnlySpan<byte> buffer, int offset)
    {
        if (_isFloat) return BitConverter.ToSingle(buffer.Slice(offset, 4));
        if (_bitsPerSample == 16) return BitConverter.ToInt16(buffer.Slice(offset, 2)) / 32768f;
        if (_bitsPerSample == 32) return BitConverter.ToInt32(buffer.Slice(offset, 4)) / 2147483648f;
        return 0;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Engine stopped (intentional or error). Watchdog will re-arm if needed.
    }

    private void EmitFrame()
    {
        if (!IsRunning) return;
        var listeners = FrameReady;
        if (listeners is null) return;

        var window = new float[_windowSamples];
        int writeIndex = _ringWrite;
        int start = (writeIndex - _windowSamples + _ring.Length) % _ring.Length;
        for (int i = 0; i < _windowSamples; i++)
            window[i] = _ring[(start + i) % _ring.Length];

        float peak = 0, rmsSum = 0;
        for (int i = 0; i < window.Length; i++)
        {
            float abs = MathF.Abs(window[i]);
            if (abs > peak) peak = abs;
            rmsSum += window[i] * window[i];
        }
        float rms = MathF.Sqrt(rmsSum / window.Length);

        var analysis = _fft.Analyze(window, _sampleRate);
        var frame = new AudioFrame(window, analysis.Spectrum, analysis.Bands, rms, peak, analysis.Bass, analysis.Mid, analysis.Treble, analysis.Beat, _sampleRate, Environment.TickCount64);

        try { listeners(frame); } catch { /* visualizer must not crash capture */ }
    }

    private void SetStatus(string status)
    {
        StatusText = status;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Waverider");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "capture.log"), $"{DateTime.Now:HH:mm:ss} {status}{Environment.NewLine}");
        }
        catch { /* diagnostics only */ }
        StatusChanged?.Invoke(status);
    }

    public void Dispose() => Stop();
}
