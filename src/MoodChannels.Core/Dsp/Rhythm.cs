namespace MoodChannels.Dsp;

/// <summary>
/// Tempo, beat regularity and onset rate from a spectral-flux onset envelope (64 ms frames, 8 ms hop at 16 kHz).
///   envelope : sum over bins of the rise in log(1 + 1000·|X|) since the previous frame; the 1 s moving average is
///              subtracted, negatives clipped, then scaled to unit standard deviation
///   tempo    : autocorrelation of the envelope over 40–240 BPM, weighted by a log-normal preference around
///              120 BPM (one octave wide), peak refined by a parabolic fit
///   beats    : Ellis (2007) dynamic programming — each beat maximises envelope strength minus
///              Tightness·ln(interval / period)²
///   dsp.bpm              : 60 / mean beat interval (intervals within 20% of the median), else the tempo estimate
///   dsp.beat_interval_cv : standard deviation / mean of all beat intervals (0 = metronomic)
///   dsp.onset_rate       : envelope peaks per second (local maxima ≥ 1 SD, at least 32 ms apart)
///   dsp.tempo_acf        : the autocorrelation tempo estimate itself (before beat tracking)
///   dsp.pulse_clarity    : unweighted autocorrelation at that period relative to lag 0 (0 … 1)
///   dsp.tempo_sal_&lt;f&gt;   : how strong the tempo bpm·f is relative to the chosen one, for the usual metrical
///                          confusions (f = 1/2, 2/3, 3/4, 4/3, 3/2, 2): autocorrelation at lag
///                          period/f over autocorrelation at period (linear interpolation between lags)
///   dsp.tempo_comb_&lt;f&gt;  : the same with a comb, A(l) = Σ_{k=1..4} acf(k·l)/k, which also counts the bar level
/// </summary>
public sealed class Rhythm
{
    const int N = 1024, Hop = 128, Bins = N / 2 + 1;
    const double Fps = (double)Audio.SampleRate / Hop;
    const double MinBpm = 40, MaxBpm = 240, PreferredBpm = 120, PreferenceOctaves = 1.0, Tightness = 100;
    static readonly (string name, double f)[] Alternatives =
        { ("0.5", 0.5), ("0.667", 2.0 / 3), ("0.75", 0.75), ("1.333", 4.0 / 3), ("1.5", 1.5), ("2", 2.0) };
    const int CombTerms = 4;

    readonly Fft _fft = new(N);
    readonly double[] _win = Fft.Hann(N);
    readonly double[] _mag = new double[Bins], _log = new double[Bins], _prev = new double[Bins];

    public void Compute(ReadOnlySpan<float> x, IDictionary<string, double> features)
    {
        features["dsp.bpm"] = double.NaN;
        features["dsp.beat_interval_cv"] = double.NaN;
        features["dsp.onset_rate"] = double.NaN;
        features["dsp.tempo_acf"] = double.NaN;
        features["dsp.pulse_clarity"] = double.NaN;
        foreach (var (name, _) in Alternatives)
        {
            features["dsp.tempo_sal_" + name] = double.NaN;
            features["dsp.tempo_comb_" + name] = double.NaN;
        }
        double[] o = Envelope(x);
        int lagMin = (int)Math.Floor(Fps * 60 / MaxBpm), lagMax = (int)Math.Ceiling(Fps * 60 / MinBpm);
        if (o.Length < 2 * lagMax) return;
        features["dsp.onset_rate"] = OnsetRate(o) / (x.Length / (double)Audio.SampleRate);

        // tempo: weighted autocorrelation
        var score = new double[lagMax + 2];
        var raw = new double[lagMax + 2];
        double r0 = 0;
        for (int t = 0; t < o.Length; t++) r0 += o[t] * o[t];
        r0 /= o.Length;
        for (int l = lagMin - 1; l <= lagMax + 1; l++)
        {
            double r = 0;
            for (int t = 0; t + l < o.Length; t++) r += o[t] * o[t + l];
            r /= o.Length - l;
            raw[l] = r;
            double oct = Math.Log2(Fps * 60 / l / PreferredBpm) / PreferenceOctaves;
            score[l] = r * Math.Exp(-0.5 * oct * oct);
        }
        int best = lagMin;
        for (int l = lagMin; l <= lagMax; l++) if (score[l] > score[best]) best = l;
        double den = score[best - 1] - 2 * score[best] + score[best + 1];
        double period = best + (den != 0 ? Math.Clamp(0.5 * (score[best - 1] - score[best + 1]) / den, -0.5, 0.5) : 0);
        if (!(period > 0)) return;
        features["dsp.bpm"] = Fps * 60 / period;
        features["dsp.tempo_acf"] = Fps * 60 / period;
        features["dsp.pulse_clarity"] = r0 > 0 ? raw[best] / r0 : 0;
        Salience(o, period, features);

        var beats = TrackBeats(o, period);
        if (beats.Count < 6) return;
        var iv = new List<double>();
        for (int i = 2; i < beats.Count - 1; i++) iv.Add((beats[i] - beats[i - 1]) / Fps);   // drop edge beats
        double med = Signal.Median(iv);
        var good = iv.Where(v => v >= 0.8 * med && v <= 1.2 * med).ToList();
        if (good.Count > 0) features["dsp.bpm"] = 60 / good.Average();
        double mean = iv.Average();
        features["dsp.beat_interval_cv"] = Math.Sqrt(iv.Select(v => (v - mean) * (v - mean)).Average()) / mean;
    }

    // autocorrelation up to the longest lag the comb needs: CombTerms · 2 · period
    static void Salience(double[] o, double period, IDictionary<string, double> features)
    {
        int maxLag = Math.Min(o.Length - 1, (int)Math.Ceiling(CombTerms * period / Alternatives.Min(a => a.f)) + 1);
        var acf = new double[maxLag + 1];
        for (int l = 0; l <= maxLag; l++)
        {
            double r = 0;
            for (int t = 0; t + l < o.Length; t++) r += o[t] * o[t + l];
            acf[l] = r / (o.Length - l);
        }
        double At(double lag)
        {
            if (lag < 0 || lag > maxLag) return double.NaN;
            int i = Math.Min((int)lag, maxLag - 1);
            double w = lag - i;
            return acf[i] * (1 - w) + acf[i + 1] * w;
        }
        double Comb(double lag)
        {
            double s = 0;
            for (int k = 1; k <= CombTerms; k++) s += At(k * lag) / k;
            return s;
        }
        double a0 = At(period), c0 = Comb(period);
        foreach (var (name, f) in Alternatives)
        {
            features["dsp.tempo_sal_" + name] = a0 > 0 ? At(period / f) / a0 : double.NaN;
            features["dsp.tempo_comb_" + name] = c0 > 0 ? Comb(period / f) / c0 : double.NaN;
        }
    }

    double[] Envelope(ReadOnlySpan<float> x)
    {
        int frames = x.Length >= N ? (x.Length - N) / Hop + 1 : 0;
        var env = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            _fft.Magnitude(x.Slice(f * Hop, N), _win, _mag);
            double flux = 0;
            for (int k = 1; k < Bins; k++)
            {
                _log[k] = Math.Log(1 + 1000 * _mag[k]);
                if (f > 0 && _log[k] > _prev[k]) flux += _log[k] - _prev[k];
            }
            Array.Copy(_log, _prev, Bins);
            env[f] = flux;
        }
        var mean = Signal.MovingMean(env, (int)Math.Round(Fps / 2));
        double sq = 0;
        for (int i = 0; i < frames; i++)
        {
            env[i] = Math.Max(0, env[i] - mean[i]);
            sq += env[i] * env[i];
        }
        double sd = frames > 0 ? Math.Sqrt(sq / frames) : 0;
        if (sd > 0) for (int i = 0; i < frames; i++) env[i] /= sd;
        return env;
    }

    static int OnsetRate(double[] o)
    {
        const int Half = 3, MinGap = 4;
        int count = 0, last = -MinGap;
        for (int t = 0; t < o.Length; t++)
        {
            if (o[t] < 1.0 || t - last < MinGap) continue;
            bool peak = true;
            for (int k = Math.Max(0, t - Half); k <= Math.Min(o.Length - 1, t + Half) && peak; k++)
                if (o[k] > o[t]) peak = false;
            if (peak) { count++; last = t; }
        }
        return count;
    }

    // Ellis, "Beat Tracking by Dynamic Programming" (2007)
    static List<int> TrackBeats(double[] o, double period)
    {
        int n = o.Length, dMin = Math.Max(1, (int)Math.Round(period / 2)), dMax = (int)Math.Round(2 * period);
        var penalty = new double[dMax + 1];
        for (int d = dMin; d <= dMax; d++)
        {
            double l = Math.Log(d / period);
            penalty[d] = Tightness * l * l;
        }
        var c = new double[n];
        var back = new int[n];
        for (int t = 0; t < n; t++)
        {
            double best = 0;
            int arg = -1;
            for (int d = dMin; d <= dMax && t - d >= 0; d++)
            {
                double v = c[t - d] - penalty[d];
                if (v > best) { best = v; arg = t - d; }
            }
            c[t] = o[t] + best;
            back[t] = arg;
        }
        int end = n - 1;
        for (int t = Math.Max(0, n - (int)Math.Round(period)); t < n; t++) if (c[t] > c[end]) end = t;
        var beats = new List<int>();
        for (int t = end; t >= 0; t = back[t]) beats.Add(t);
        beats.Reverse();
        return beats;
    }
}
