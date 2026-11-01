using FftSharp;

namespace Waverider.Audio;

public sealed class FftAnalyzer
{
    private readonly int _fftSize;
    private readonly int _bandCount;
    private readonly double[] _window;
    private readonly double[] _buffer;
    private readonly float[] _smoothedBands;
    private readonly float _smoothing;

    private readonly float[] _bassHistory = new float[60];
    private int _bassHistoryIndex;
    private float _beatEnvelope;
    private long _lastBeatMs;

    public FftAnalyzer(int fftSize = 2048, int bandCount = 64, float smoothing = 0.6f)
    {
        if ((fftSize & (fftSize - 1)) != 0)
            throw new ArgumentException("fftSize must be a power of two", nameof(fftSize));

        _fftSize = fftSize;
        _bandCount = bandCount;
        _smoothing = Math.Clamp(smoothing, 0f, 0.99f);
        _window = new FftSharp.Windows.Hanning().Create(fftSize);
        _buffer = new double[fftSize];
        _smoothedBands = new float[bandCount];
    }

    public int FftSize => _fftSize;
    public int BandCount => _bandCount;

    public AnalysisResult Analyze(ReadOnlySpan<float> mono, int sampleRate)
    {
        int n = Math.Min(mono.Length, _fftSize);
        for (int i = 0; i < n; i++) _buffer[i] = mono[i] * _window[i];
        for (int i = n; i < _fftSize; i++) _buffer[i] = 0;

        var spectrumD = FFT.ForwardReal(_buffer);
        var spectrum = new float[spectrumD.Length];
        for (int i = 0; i < spectrumD.Length; i++)
        {
            float v = (float)Math.Sqrt(spectrumD[i].Real * spectrumD[i].Real + spectrumD[i].Imaginary * spectrumD[i].Imaginary);
            spectrum[i] = v / _fftSize;
        }

        var bands = ComputeBands(spectrum, sampleRate);
        for (int i = 0; i < _bandCount; i++)
        {
            _smoothedBands[i] = _smoothedBands[i] * _smoothing + bands[i] * (1 - _smoothing);
            bands[i] = _smoothedBands[i];
        }

        // Split into bass/mid/treble averages from the smoothed bands.
        // 64 bands log-spaced from ~30Hz to Nyquist ⇒ first ~10 are sub-bass/bass,
        // middle covers vocals/instruments, last quarter covers cymbals/air.
        int bassEnd = Math.Max(2, _bandCount * 1 / 6);
        int midEnd = Math.Max(bassEnd + 1, _bandCount * 4 / 6);

        float bass = 0, mid = 0, treble = 0;
        for (int i = 0; i < bassEnd; i++) bass += bands[i];
        for (int i = bassEnd; i < midEnd; i++) mid += bands[i];
        for (int i = midEnd; i < _bandCount; i++) treble += bands[i];
        bass /= bassEnd;
        mid /= midEnd - bassEnd;
        treble /= _bandCount - midEnd;

        // Energy-based beat detection on the bass band: a beat is registered when
        // current bass exceeds 1.4× the rolling average AND there's been at least
        // 180ms since the last beat (avoid double-fires).
        _bassHistory[_bassHistoryIndex] = bass;
        _bassHistoryIndex = (_bassHistoryIndex + 1) % _bassHistory.Length;
        float avg = 0;
        for (int i = 0; i < _bassHistory.Length; i++) avg += _bassHistory[i];
        avg /= _bassHistory.Length;

        long now = Environment.TickCount64;
        if (bass > avg * 1.4f && bass > 0.32f && now - _lastBeatMs > 180)
        {
            _beatEnvelope = 1f;
            _lastBeatMs = now;
        }
        _beatEnvelope = MathF.Max(0, _beatEnvelope - 0.06f);

        return new AnalysisResult(spectrum, bands, bass, mid, treble, _beatEnvelope);
    }

    private float[] ComputeBands(float[] spectrum, int sampleRate)
    {
        const float minHz = 30f;
        float maxHz = sampleRate * 0.5f;
        float logMin = MathF.Log(minHz);
        float logMax = MathF.Log(maxHz);
        float step = (logMax - logMin) / _bandCount;
        float binHz = sampleRate / (float)_fftSize;
        var bands = new float[_bandCount];

        for (int b = 0; b < _bandCount; b++)
        {
            float fLo = MathF.Exp(logMin + step * b);
            float fHi = MathF.Exp(logMin + step * (b + 1));
            int loBin = Math.Max(1, (int)MathF.Floor(fLo / binHz));
            int hiBin = Math.Min(spectrum.Length - 1, (int)MathF.Ceiling(fHi / binHz));
            if (hiBin < loBin) hiBin = loBin;

            float sum = 0;
            for (int i = loBin; i <= hiBin; i++) sum = MathF.Max(sum, spectrum[i]);

            float db = 20f * MathF.Log10(sum + 1e-6f);
            float norm = Math.Clamp((db + 80f) / 80f, 0f, 1f);
            bands[b] = norm;
        }
        return bands;
    }

    public readonly record struct AnalysisResult(
        float[] Spectrum,
        float[] Bands,
        float Bass,
        float Mid,
        float Treble,
        float Beat);
}
