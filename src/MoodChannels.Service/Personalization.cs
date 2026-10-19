using System.Text.Json;
using System.Text.Json.Nodes;

namespace MoodChannels.Service;

/// <summary>
/// The listener's own idea of each channel, learnt from their 👍 / 👎 on tracks in it (AnalysisStore votes):
/// <list type="bullet">
/// <item>a 👎 takes the track out of that channel, a 👍 keeps it there whatever else says;</item>
/// <item>once a channel has at least <see cref="MinEach"/> of each, a model of it is trained on them (L2 logistic
///       regression on the DSP descriptors, standardised; Newton's method, a few milliseconds) and takes out the
///       tracks without a vote that it finds do not belong (p ≥ <see cref="Threshold"/>);</item>
/// <item>a track taken out of its time zone or of all its moods goes to the next one the channel model prefers.</item>
/// </list>
/// Built-in tuning (models\basic\tuning.json, <see cref="LoadBuiltIn"/>): channel models shipped with the module,
/// learnt from a listener's votes (<see cref="Export"/>; weights only, no tracks). While on (<see cref="UseBuiltIn"/>)
/// a channel without enough votes of its own uses the built-in model, and one with enough is trained starting from it
/// (the regularisation pulls towards the built-in weights instead of towards none): with a few votes the channel
/// stays close to the built-in tuning, with many it follows the listener. The listener's own votes always decide
/// their tracks.
/// It only ever takes tracks out of a channel, never puts them into one. Retrained after every vote; all of it runs
/// here, nothing leaves the computer. Thread-safe.
/// </summary>
sealed class Personalization
{
    public const int MinEach = 5;
    public const double Threshold = 0.5;
    const double C = 0.1;                       // regularisation (as scikit-learn's C): small data must not swing a channel

    /// <summary>One channel's model: p(does not belong) for a track's descriptors.</summary>
    sealed class Model
    {
        public required double[] Fill, Mean, Scale, W;
        public required double B;

        public double NotBelong(float[] f)
        {
            double z = B;
            for (int i = 0; i < W.Length; i++)
            {
                double x = i < f.Length && float.IsFinite(f[i]) ? f[i] : Fill[i];
                z += W[i] * (x - Mean[i]) / Scale[i];
            }
            return 1 / (1 + Math.Exp(-z));
        }
    }

    readonly AnalysisStore _store;
    readonly Dictionary<int, Model> _models = new();
    readonly Dictionary<int, Model> _builtIn = new();
    readonly Dictionary<int, (int Up, int Down)> _counts = new();
    readonly object _lock = new();
    bool _useBuiltIn = true;

    public Personalization(AnalysisStore store) => _store = store;

    /// <summary>Changes on every training: results worked out with another version are stale.</summary>
    public int Version { get; private set; }

    /// <summary>The built-in tuning's file format (tuning.json "format").</summary>
    const int TuningFormat = 1;

    /// <summary>
    /// Reads the built-in tuning; returns what happened, for the log. It is left out when it was learnt from other
    /// descriptors (another DSP version or feature list): its weights would not mean the same.
    /// </summary>
    public string LoadBuiltIn(string file, IReadOnlyList<string> features)
    {
        if (!File.Exists(file)) return "no built-in tuning";
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file))!;
            if ((int?)root["format"] != TuningFormat || (string?)root["dsp"] != Dsp.DspFeatures.Version
                || !root["features"]!.AsArray().Select(x => (string?)x).SequenceEqual(features))
                return "built-in tuning not used: learnt from other descriptors";
            static double[] D(JsonNode? n) => n!.AsArray().Select(x => (double)x!).ToArray();
            var loaded = new Dictionary<int, Model>();
            foreach (var (key, c) in root["channels"]!.AsObject())
                loaded[int.Parse(key)] = new Model { Fill = D(c!["fill"]), Mean = D(c["mean"]), Scale = D(c["scale"]), W = D(c["w"]), B = (double)c["b"]! };
            lock (_lock)
            {
                _builtIn.Clear();
                foreach (var kv in loaded) _builtIn[kv.Key] = kv.Value;
            }
            return $"built-in tuning: {string.Join(", ", loaded.Keys.Order().Select(k => Channels.Names[k]))}";
        }
        catch (Exception ex)
        {
            return "built-in tuning not read: " + ex.Message;
        }
    }

    /// <summary>Whether the built-in tuning is used (the setting); changing it trains every channel again.</summary>
    public bool UseBuiltIn
    {
        get { lock (_lock) return _useBuiltIn; }
        set
        {
            lock (_lock) { if (_useBuiltIn == value) return; _useBuiltIn = value; }
            Train();
        }
    }

    /// <summary>The channels the built-in tuning has a model of.</summary>
    public IReadOnlyList<string> BuiltInChannels { get { lock (_lock) return _builtIn.Keys.Order().Select(k => Channels.Names[k]).ToList(); } }

    Model? Prior(int channel) { lock (_lock) return _useBuiltIn && _builtIn.TryGetValue(channel, out var m) ? m : null; }

    /// <summary>The model that decides a channel's tracks without a vote: the listener's, else the built-in one.</summary>
    Model? ModelFor(int channel)
    {
        lock (_lock)
            return _models.TryGetValue(channel, out var m) ? m : _useBuiltIn && _builtIn.TryGetValue(channel, out var b) ? b : null;
    }

    List<(float[] F, int Vote)> Rows(int channel) =>
        _store.Votes(channel).Select(v => (f: _store.FeatureVector(v.Entry), v.Vote)).Where(r => r.f != null).Select(r => (r.f!, r.Vote)).ToList();

    /// <summary>Trains the models of these channels (default: all) from the stored votes.</summary>
    public void Train(params int[] channels)
    {
        foreach (int ch in channels.Length > 0 ? channels : Enumerable.Range(0, Channels.Count).ToArray())
        {
            var rows = Rows(ch);
            int up = rows.Count(r => r.Vote > 0), down = rows.Count(r => r.Vote < 0);
            Model? m = up >= MinEach && down >= MinEach
                ? Fit(rows.Select(r => r.F).ToArray(), rows.Select(r => r.Vote < 0 ? 1.0 : 0.0).ToArray(), Prior(ch)) : null;
            lock (_lock)
            {
                _counts[ch] = (up, down);
                if (m != null) _models[ch] = m; else _models.Remove(ch);
            }
        }
        lock (_lock) Version++;
    }

    /// <summary>
    /// The listener's channel models as a built-in tuning (tuning.json): learnt from their votes alone (not from a
    /// built-in one), for the channels with enough votes. Weights, standardisation and vote counts only: no tracks.
    /// </summary>
    public JsonObject Export(IReadOnlyList<string> features)
    {
        var channels = new JsonObject();
        for (int ch = 0; ch < Channels.Count; ch++)
        {
            var rows = Rows(ch);
            int up = rows.Count(r => r.Vote > 0), down = rows.Count(r => r.Vote < 0);
            if (up < MinEach || down < MinEach) continue;
            var m = Fit(rows.Select(r => r.F).ToArray(), rows.Select(r => r.Vote < 0 ? 1.0 : 0.0).ToArray(), null);
            JsonArray A(double[] v) => new(v.Select(x => (JsonNode)JsonValue.Create(Math.Round(x, 9))).ToArray());
            channels[ch.ToString()] = new JsonObject
            {
                ["name"] = Channels.Names[ch], ["up"] = up, ["down"] = down,
                ["fill"] = A(m.Fill), ["mean"] = A(m.Mean), ["scale"] = A(m.Scale), ["w"] = A(m.W), ["b"] = Math.Round(m.B, 9),
            };
        }
        return new JsonObject
        {
            ["comment"] = "Built-in channel tuning: logistic models of p(a track does not belong to the channel) on the standardised DSP descriptors, learnt from a listener's votes. Weights only; no tracks.",
            ["format"] = TuningFormat, ["dsp"] = Dsp.DspFeatures.Version, ["threshold"] = Threshold, ["c"] = C,
            ["exported"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["features"] = new JsonArray(features.Select(f => (JsonNode)JsonValue.Create(f)).ToArray()),
            ["channels"] = channels,
        };
    }

    /// <summary>For the page: a channel's votes, whether its model is learning yet, and whether a built-in one is used.</summary>
    public object Status(int channel)
    {
        lock (_lock)
        {
            var (up, down) = _counts.TryGetValue(channel, out var c) ? c : (0, 0);
            return new { up, down, learning = _models.ContainsKey(channel), builtIn = _useBuiltIn && _builtIn.ContainsKey(channel), needEach = MinEach };
        }
    }

    /// <summary>Whether the listener takes this track out of this channel (their vote, else the channel's model).</summary>
    public bool Rejects(Entry e, int channel)
    {
        int vote = _store.Vote(e.Path, channel);
        if (vote != 0) return vote < 0;
        var m = ModelFor(channel);
        if (m == null) return false;
        var f = _store.FeatureVector(e);
        return f != null && m.NotBelong(f) >= Threshold;
    }

    /// <summary>Whether any channel of these has something that can take a track out (votes or a model).</summary>
    public bool Touches(Entry e, int[] channels)
    {
        if (channels.Any(c => ModelFor(c) != null)) return true;
        return channels.Any(c => _store.Vote(e.Path, c) != 0);
    }

    /// <summary>
    /// The track's channels after the listener's votes and models. <paramref name="probabilities"/> gives the channel
    /// model's probabilities, asked only when a track must go to another time zone or mood.
    /// </summary>
    public int[] Adjust(Entry e, int[] channels, ChannelModel model, Func<double[]> probabilities)
    {
        if (channels.Length == 0 || !Touches(e, channels)) return channels;
        var keep = channels.Where(c => !Rejects(e, c)).ToList();
        if (keep.Count == channels.Length) return channels;
        double[]? p = null;
        if (!keep.Any(Channels.IsTimeZone) && channels.Any(Channels.IsTimeZone))
        {
            p ??= probabilities();
            int z = model.ZoneOrder(p).FirstOrDefault(c => !channels.Contains(c) && !Rejects(e, c), -1);
            if (z >= 0) keep.Insert(0, z);
        }
        if (!keep.Any(Channels.IsMood) && channels.Any(Channels.IsMood))
        {
            p ??= probabilities();
            int m = model.MoodOrder(p).FirstOrDefault(c => !channels.Contains(c) && !Rejects(e, c), -1);
            if (m >= 0) keep.Add(m);
        }
        return keep.ToArray();
    }

    /// <summary>
    /// L2 logistic regression (Newton's method), standardised inputs, NaN filled with the column median. With a
    /// <paramref name="prior"/> (a built-in model of the channel): its standardisation, and the weights pulled towards
    /// its weights instead of towards zero.
    /// </summary>
    static Model Fit(float[][] rows, double[] y, Model? prior)
    {
        int n = rows.Length, d = prior?.W.Length ?? rows.Max(r => r.Length);
        var fill = new double[d];
        for (int j = 0; j < d; j++)
        {
            if (prior != null) { fill[j] = prior.Fill[j]; continue; }
            var col = rows.Select(r => j < r.Length ? (double)r[j] : double.NaN).Where(double.IsFinite).OrderBy(v => v).ToArray();
            fill[j] = col.Length == 0 ? 0 : col.Length % 2 == 1 ? col[col.Length / 2] : (col[col.Length / 2 - 1] + col[col.Length / 2]) / 2;
        }
        var x = new double[n, d + 1];                        // standardised, with the intercept's column last
        var mean = new double[d];
        var scale = new double[d];
        for (int j = 0; j < d; j++)
        {
            if (prior != null) (mean[j], scale[j]) = (prior.Mean[j], prior.Scale[j]);
            else
            {
                double s = 0, s2 = 0;
                for (int i = 0; i < n; i++) { double v = Value(rows[i], j, fill); s += v; s2 += v * v; }
                mean[j] = s / n;
                double var = s2 / n - mean[j] * mean[j];
                scale[j] = var > 1e-24 ? Math.Sqrt(var) : 1;
            }
            for (int i = 0; i < n; i++) x[i, j] = (Value(rows[i], j, fill) - mean[j]) / scale[j];
        }
        for (int i = 0; i < n; i++) x[i, d] = 1;
        int k = d + 1;
        var w0 = new double[k];                              // what the weights are pulled towards
        if (prior != null) { Array.Copy(prior.W, w0, d); w0[d] = prior.B; }
        var w = (double[])w0.Clone();
        for (int it = 0; it < 50; it++)
        {
            var g = new double[k];
            var h = new double[k, k];
            for (int i = 0; i < n; i++)
            {
                double z = 0;
                for (int j = 0; j < k; j++) z += x[i, j] * w[j];
                double p = 1 / (1 + Math.Exp(-z)), r = p * (1 - p), e = p - y[i];
                for (int a = 0; a < k; a++)
                {
                    g[a] += x[i, a] * e;
                    for (int b = a; b < k; b++) h[a, b] += x[i, a] * x[i, b] * r;
                }
            }
            for (int a = 0; a < k; a++)
            {
                for (int b = 0; b < a; b++) h[a, b] = h[b, a];
                if (a < d) { g[a] += (w[a] - w0[a]) / C; h[a, a] += 1 / C; }   // the intercept is not penalised
            }
            var step = Solve(h, g);
            double largest = 0;
            for (int a = 0; a < k; a++) { w[a] -= step[a]; largest = Math.Max(largest, Math.Abs(step[a])); }
            if (largest < 1e-10) break;
        }
        return new Model { Fill = fill, Mean = mean, Scale = scale, W = w[..d], B = w[d] };
    }

    static double Value(float[] r, int j, double[] fill) => j < r.Length && float.IsFinite(r[j]) ? r[j] : fill[j];

    /// <summary>Solves a·x = b (Gaussian elimination with partial pivoting; a is symmetric positive definite here).</summary>
    static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var v = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            if (piv != c)
            {
                for (int j = 0; j < n; j++) (m[c, j], m[piv, j]) = (m[piv, j], m[c, j]);
                (v[c], v[piv]) = (v[piv], v[c]);
            }
            for (int r = c + 1; r < n; r++)
            {
                double f = m[r, c] / m[c, c];
                if (f == 0) continue;
                for (int j = c; j < n; j++) m[r, j] -= f * m[c, j];
                v[r] -= f * v[c];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = v[r];
            for (int j = r + 1; j < n; j++) s -= m[r, j] * x[j];
            x[r] = s / m[r, r];
        }
        return x;
    }
}
