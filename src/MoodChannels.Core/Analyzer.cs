using MoodChannels.Dsp;

namespace MoodChannels;

/// <summary>What the audio gives: every DSP descriptor (dsp.*) and duration, and the key. Independent of the model.</summary>
public sealed record TrackFeatures(
    IReadOnlyDictionary<string, double> Values,     // dsp.* and "duration"
    string Key,                                     // e.g. "A minor"; "" when undefined
    double DurationSeconds);                        // of the analysed audio (at most Audio's 900 s cap)

/// <summary>Result of <see cref="Analyzer.Analyze(string)"/>.</summary>
public sealed record TrackAnalysis(ChannelPrediction Prediction, TrackFeatures Features, string ModelVersion)
{
    public int[] Channels => Prediction.Channels;
    public bool Unclassifiable => Prediction.Unclassifiable;
    public double Bpm => Prediction.Bpm;
    public string Key => Features.Key;
}

/// <summary>
/// The basic engine: audio file -> 16 kHz mono (ffmpeg) -> DSP descriptors -> channel model -> channels, BPM, key.
/// No neural network. The two halves are separate so the features can be stored and the channels worked out again
/// when the model changes (<see cref="Extract(string, string?, bool)"/>, then <see cref="Classify"/>).
/// Thread-safe: call it from as many threads as you like (each thread keeps its own DSP buffers).
/// </summary>
public sealed class Analyzer : IDisposable
{
    /// <summary>Shorter audio is reported unclassifiable without running the model (the training data had none).</summary>
    public const double MinSeconds = 6;

    readonly ChannelModel _model;
    readonly ThreadLocal<DspFeatures> _dsp = new(() => new DspFeatures());

    public Analyzer(string modelDir) => _model = new ChannelModel(modelDir);

    public string ModelVersion => _model.Version;

    /// <summary>The channel model (its preference orders, for moving a track to its next best channel).</summary>
    public ChannelModel Model => _model;

    public TrackAnalysis Analyze(string path) => Analyze(Audio.Decode(path));

    /// <summary>Analysis of 16 kHz mono samples (<see cref="Audio.SampleRate"/>).</summary>
    public TrackAnalysis Analyze(float[] pcm)
    {
        var f = Extract(pcm);
        return new TrackAnalysis(Classify(f), f, _model.Version);
    }

    /// <summary>The features of an audio file (decoded with <paramref name="ffmpeg"/>, see <see cref="Audio.Decode"/>).</summary>
    public TrackFeatures Extract(string path, string? ffmpeg = null, bool lowPriority = false) =>
        Extract(Audio.Decode(path, ffmpeg: ffmpeg, lowPriority: lowPriority));

    /// <summary>The features of 16 kHz mono samples. Computed with <see cref="DspFeatures.Version"/>.</summary>
    public TrackFeatures Extract(float[] pcm)
    {
        double seconds = pcm.Length / (double)Audio.SampleRate;
        if (seconds < MinSeconds)
            return new TrackFeatures(new Dictionary<string, double> { ["duration"] = seconds }, "", seconds);
        var values = new Dictionary<string, double>(_dsp.Value!.Compute(pcm, out string key), StringComparer.Ordinal)
        {
            ["duration"] = seconds,
        };
        return new TrackFeatures(values, key, seconds);
    }

    /// <summary>The channels for features computed with the current <see cref="DspFeatures.Version"/>.</summary>
    public ChannelPrediction Classify(TrackFeatures f)
    {
        if (f.DurationSeconds < MinSeconds)
            return new ChannelPrediction(Array.Empty<int>(), true, 0, Array.Empty<int>(), new double[MoodChannels.Channels.Count], 1, double.NaN);
        return _model.Predict(f.Values);
    }

    public void Dispose()
    {
        _model.Dispose();
        _dsp.Dispose();
    }
}
