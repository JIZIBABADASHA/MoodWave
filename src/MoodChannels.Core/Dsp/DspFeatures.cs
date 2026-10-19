namespace MoodChannels.Dsp;

/// <summary>
/// All DSP descriptors the channel model uses, from 16 kHz mono samples (see each class for its definition).
/// Keys are "dsp.*". Holds FFT buffers: use one instance per thread.
/// </summary>
public sealed class DspFeatures
{
    /// <summary>
    /// Changes whenever a descriptor's definition changes: stored features computed by another version must be
    /// computed again (the channel models are trained on one version).
    /// </summary>
    public const string Version = "1";

    static string[]? _names;

    /// <summary>The descriptor names <see cref="Compute"/> returns, in its (ordinal) order.</summary>
    public static IReadOnlyList<string> Names =>
        _names ??= new DspFeatures().Compute(new float[Audio.SampleRate * 10], out _).Keys.ToArray();

    readonly SpectralStats _spectral = new();
    readonly Rhythm _rhythm = new();
    readonly KeyDetector _key = new();

    /// <summary>Feature values (NaN where undefined, e.g. no beats) and the detected key name.</summary>
    public SortedDictionary<string, double> Compute(ReadOnlySpan<float> pcm, out string key)
    {
        var f = new SortedDictionary<string, double>(StringComparer.Ordinal);
        _spectral.Compute(pcm, f);
        _rhythm.Compute(pcm, f);
        Fluctuation.Compute(pcm, f);
        key = _key.Compute(pcm, f);
        Loudness.Compute(pcm, f);
        return f;
    }
}
