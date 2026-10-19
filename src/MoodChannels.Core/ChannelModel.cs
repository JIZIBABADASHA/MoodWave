using System.Security.Cryptography;
using System.Text.Json;

namespace MoodChannels;

/// <summary>Channel decision for one track, from <see cref="ChannelModel.Predict"/>.</summary>
public sealed record ChannelPrediction(
    int[] Channels,                 // native indices: one time zone + one or two moods; empty when unclassifiable
    bool Unclassifiable,
    int TimeZone,                   // 0–4, the zone the track would get (also set when unclassifiable)
    int[] Moods,                    // native indices 5–12
    double[] Probabilities,         // 13: time-zone distribution (0–4), then each mood's own probability (5–12)
    double UnclassifiableProbability,
    double Bpm);                    // the DSP tempo times the predicted metrical factor; NaN without a beat

/// <summary>
/// The trained channel classifiers: a model directory with model.json and the tree files it lists (models.*.trees_file,
/// see <see cref="TreeModel"/>). Applies the decision rules stored in model.json exactly as the training defines them:
/// <list type="bullet">
/// <item>unclassifiable when p(none) ≥ unclassifiable_threshold;</item>
/// <item>time zone = argmax p[zone] · time_zone_weights[zone] (each zone gets its usual share of tracks);</item>
/// <item>moods: w = p[mood] · mood_weights[mood]; the highest w, plus the second when its w ≥ second_mood_threshold
///       (older models without mood_weights: every mood with p ≥ its threshold, else the highest p);</item>
/// <item>BPM = dsp.bpm · factors[argmax of the bpm_octave model].</item>
/// </list>
/// Thread-safe.
/// </summary>
public sealed class ChannelModel : IDisposable
{
    readonly Dictionary<string, TreeModel> _models = new();
    readonly string[] _features;
    readonly double _noneThreshold;
    readonly double[] _zoneWeights;
    readonly double[]? _moodWeights;
    readonly double _secondMood;
    readonly double[] _moodThresholds;
    readonly double[] _bpmFactors;
    readonly string _bpmInput;

    /// <summary>The input feature names, in model order.</summary>
    public IReadOnlyList<string> Features => _features;

    /// <summary>First 12 hex digits of model.json's SHA-256: store it with results to know when to re-run them.</summary>
    public string Version { get; }

    public ChannelModel(string modelDir)
    {
        var jsonPath = Path.Combine(modelDir, "model.json");
        var bytes = File.ReadAllBytes(jsonPath);
        Version = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        using var meta = JsonDocument.Parse(bytes);
        var root = meta.RootElement;
        if (root.TryGetProperty("stack", out var stack) && stack.ValueKind != JsonValueKind.Null)
            throw new NotSupportedException("this model needs the advanced (CLAP) features, which this engine does not compute yet");

        _features = root.GetProperty("features").EnumerateArray().Select(e => e.GetString()!).ToArray();
        foreach (var m in root.GetProperty("models").EnumerateObject())
            _models[m.Name] = m.Value.TryGetProperty("trees_file", out var tf)
                ? new TreeModel(Path.Combine(modelDir, tf.GetString()!))
                : throw new InvalidDataException($"model.json: {m.Name} has no trees_file (export it with the training's export_trees.py)");
        _noneThreshold = root.GetProperty("unclassifiable_threshold").GetDouble();
        _zoneWeights = PerChannel(root, "time_zone_weights", 0, Channels.TimeZoneCount) ?? Enumerable.Repeat(1.0, 5).ToArray();
        _moodWeights = PerChannel(root, "mood_weights", Channels.FirstMood, Channels.MoodCount);
        _secondMood = root.TryGetProperty("second_mood_threshold", out var t2) ? t2.GetDouble() : double.PositiveInfinity;
        _moodThresholds = PerChannel(root, "mood_thresholds", Channels.FirstMood, Channels.MoodCount)
                          ?? throw new InvalidDataException("model.json has neither mood_weights nor mood_thresholds");
        var bpm = root.GetProperty("bpm_octave");
        _bpmFactors = bpm.GetProperty("factors").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        _bpmInput = bpm.GetProperty("input_bpm").GetString()!;
    }

    static double[]? PerChannel(JsonElement root, string name, int first, int count) =>
        root.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object
            ? Enumerable.Range(first, count).Select(c => o.GetProperty(Channels.Names[c]).GetDouble()).ToArray()
            : null;

    /// <summary>Channels for one track from its features (missing or non-finite values count as missing).</summary>
    public ChannelPrediction Predict(IReadOnlyDictionary<string, double> features)
    {
        var x = new float[_features.Length];
        for (int i = 0; i < x.Length; i++)
            x[i] = features.TryGetValue(_features[i], out double v) && double.IsFinite(v) ? (float)v : float.NaN;

        double pNone = Probabilities("none", x)[1];
        var tz = Probabilities("time_zone", x);
        var p = new double[Channels.Count];
        for (int c = 0; c < Channels.TimeZoneCount; c++) p[c] = tz[c];
        for (int j = 0; j < Channels.MoodCount; j++)
            p[Channels.FirstMood + j] = Probabilities("mood_" + Channels.Names[Channels.FirstMood + j], x)[1];

        int zone = Enumerable.Range(0, Channels.TimeZoneCount).MaxBy(c => tz[c] * _zoneWeights[c]);
        int[] moods = DecideMoods(p);
        bool none = pNone >= _noneThreshold;
        var channels = none ? Array.Empty<int>() : moods.Prepend(zone).ToArray();

        var octave = Probabilities("bpm_octave", x);
        double raw = features.TryGetValue(_bpmInput, out double b) ? b : double.NaN;
        double bpm = raw * _bpmFactors[Enumerable.Range(0, octave.Length).MaxBy(i => octave[i])];
        return new ChannelPrediction(channels, none, zone, moods, p, pNone, bpm);
    }

    /// <summary>The time zones (0–4) by preference for these probabilities, best first (as the zone rule weighs them).</summary>
    public int[] ZoneOrder(double[] p) =>
        Enumerable.Range(0, Channels.TimeZoneCount).OrderByDescending(c => p[c] * _zoneWeights[c]).ToArray();

    /// <summary>The moods (native 5–12) by preference for these probabilities, best first (as the mood rule weighs them).</summary>
    public int[] MoodOrder(double[] p) =>
        Enumerable.Range(0, Channels.MoodCount).OrderByDescending(j => p[Channels.FirstMood + j] * (_moodWeights?[j] ?? 1))
                  .Select(j => Channels.FirstMood + j).ToArray();

    int[] DecideMoods(double[] p)
    {
        var moods = new List<int>();
        if (_moodWeights is { } w)
        {
            var order = Enumerable.Range(0, Channels.MoodCount).OrderByDescending(j => p[Channels.FirstMood + j] * w[j]).ToArray();
            moods.Add(Channels.FirstMood + order[0]);
            if (p[Channels.FirstMood + order[1]] * w[order[1]] >= _secondMood) moods.Add(Channels.FirstMood + order[1]);
            return moods.ToArray();
        }
        for (int j = 0; j < Channels.MoodCount; j++)
            if (p[Channels.FirstMood + j] >= _moodThresholds[j]) moods.Add(Channels.FirstMood + j);
        if (moods.Count == 0) moods.Add(Channels.FirstMood + Enumerable.Range(0, Channels.MoodCount).MaxBy(j => p[Channels.FirstMood + j]));
        return moods.ToArray();
    }

    double[] Probabilities(string model, float[] x) => _models[model].Predict(x);

    public void Dispose() { }
}
