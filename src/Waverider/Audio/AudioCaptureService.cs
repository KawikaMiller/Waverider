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
    private uint? _currentPid;
    private string? _appProcessName;
    private string? _appDisplayName;

    private readonly int _windowSamples;
    private readonly float[] _ring;
    private int _ringWrite;
    private int _channels = 2;
    private int _sampleRate = 48000;
    private int _bitsPerSample = 32;
    private bool _isFloat = true;

    public AudioCaptureMode Mode { get; private set; } = AudioCaptureMode.ThisApp;

    /// <summary>Executable name of the app captured in <see cref="AudioCaptureMode.Application"/> mode.</summary>
    public string? AppProcessName => _appProcessName;

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

    /// <summary>Captures one application's audio (its whole process tree), restarting capture if it was running.</summary>
    public void SetApplication(string processName, string? displayName = null)
    {
        lock (_lock)
        {
            if (Mode == AudioCaptureMode.Application && string.Equals(_appProcessName, processName, StringComparison.OrdinalIgnoreCase)) return;
            Mode = AudioCaptureMode.Application;
            _appProcessName = processName;
            _appDisplayName = displayName ?? AudioAppEnumerator.DisplayNameFor(processName);
        }
        Stop();
        Start();
    }

    /// <summary>Applies a saved selection without starting capture. Only valid before <see cref="Start"/>.</summary>
    public void Restore(AudioCaptureMode mode, string? processName)
    {
        lock (_lock)
        {
            if (IsRunning || _watchdog is not null) return;
            if (mode == AudioCaptureMode.Application)
            {
                if (string.IsNullOrWhiteSpace(processName)) return;
                _appProcessName = processName;
                _appDisplayName = AudioAppEnumerator.DisplayNameFor(processName);
            }
            Mode = mode;
        }
    }

    /// <summary>Name shown in status text for the process-based modes.</summary>
    private string TargetLabel => Mode switch
    {
        AudioCaptureMode.ThisApp => "this app's built-in player",
        AudioCaptureMode.SpotifyOnly => "Spotify",
        _ => _appDisplayName ?? _appProcessName ?? "the selected app",
    };

    private string CapturingStatus(uint? pid) =>
        Mode == AudioCaptureMode.ThisApp ? $"Capturing {TargetLabel} (PID {pid})." : $"Capturing only {TargetLabel} (PID {pid}).";

    /// <summary>PID that process-tree capture should target for the current mode, or null when that app isn't running.</summary>
    private uint? FindTargetPid() => Mode switch
    {
        // WebView2's processes are not children of this process (its browser process is launched by a
        // system service), so target that browser process directly; the audio service is a descendant of it.
        AudioCaptureMode.ThisApp => ThisAppPidProvider?.Invoke(),
        AudioCaptureMode.SpotifyOnly => AppProcessLocator.FindRootPid("Spotify"),
        AudioCaptureMode.Application when _appProcessName is not null => AppProcessLocator.FindRootPid(_appProcessName),
        _ => null,
    };

    public void Start()
    {
        lock (_lock)
        {
            if (IsRunning || _watchdog is not null) return;

            if (Mode == AudioCaptureMode.SystemLoopback)
            {
                try { StartSystemLoopback(); SetStatus("Capturing all system audio."); }
                catch (Exception ex) { SetStatus($"System loopback failed: {ex.Message}"); return; }
            }
            else
            {
                var pid = FindTargetPid();
                if (pid is null)
                {
                    SetStatus(Mode == AudioCaptureMode.ThisApp
                        ? "Waiting for the built-in player to start…"
                        : $"{TargetLabel} isn't running — capture will start automatically when it launches.");
                }
                else
                {
                    try
                    {
                        StartProcessLoopback(pid.Value);
                        SetStatus(CapturingStatus(pid));
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Process loopback failed ({ex.Message}). Falling back to all system audio.");
                        try { StartSystemLoopback(); } catch (Exception ex2) { SetStatus($"System loopback failed: {ex2.Message}"); return; }
                    }
                }
            }

            // Watchdog re-checks the target process every few seconds in the process-based modes.
            _watchdog = new System.Threading.Timer(_ => CheckTargetState(), null, 3000, 3000);
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
        _currentPid = null;

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
        _currentPid = pid;
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

    private void CheckTargetState()
    {
        if (Mode == AudioCaptureMode.SystemLoopback) return;
        uint? pid;
        try { pid = FindTargetPid(); }
        catch { return; }

        lock (_lock)
        {
            // A tick already in flight when Stop() ran must not re-arm capture.
            if (_watchdog is null) return;
            if (Mode == AudioCaptureMode.SystemLoopback) return;
            if (_wasapi is not null) return; // running on the system-loopback fallback; don't double up

            if (pid is null && _currentPid is not null)
            {
                StopEngine_NoLock();
                SetStatus(Mode == AudioCaptureMode.ThisApp
                    ? "Built-in player stopped — waiting for it to restart."
                    : $"{TargetLabel} closed — waiting for it to reopen.");
                return;
            }

            if (pid is not null && _currentPid != pid)
            {
                // The app just launched, or restarted with a different PID: (re-)arm.
                StopEngine_NoLock();
                try
                {
                    StartProcessLoopback(pid.Value);
                    SetStatus(CapturingStatus(pid));
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
