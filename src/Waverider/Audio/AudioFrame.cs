namespace Waverider.Audio;

public sealed record AudioFrame(
    float[] Samples,
    float[] Spectrum,
    float[] Bands,
    float Rms,
    float Peak,
    float Bass,
    float Mid,
    float Treble,
    float Beat,
    int SampleRate,
    long Timestamp);
