namespace MoodChannels.Dsp;

/// <summary>
/// Loudness of the mono 16 kHz signal per ITU-R BS.1770-4 and EBU Tech 3342:
///   K-weighting : a high-shelf (+4 dB above ~1.7 kHz) and a ~38 Hz high-pass, both as RBJ-cookbook biquads with
///                 the parameters that reproduce the standard's 48 kHz coefficients, designed for 16 kHz
///   dsp.loudness           : integrated loudness (LUFS) of 400 ms blocks every 100 ms, gated at −70 LUFS and then
///                            10 LU below the mean of the remaining blocks
///   dsp.loudness_range     : 95th minus 10th percentile of 3 s short-term loudness (every 100 ms), gated at −70
///                            LUFS and 20 LU below their mean (LU)
///   dsp.dynamic_complexity : mean absolute deviation of the gated 400 ms block loudness from its mean (LU)
/// </summary>
public static class Loudness
{
    const double AbsGate = -70;

    public static void Compute(ReadOnlySpan<float> x, IDictionary<string, double> features)
    {
        int sr = Audio.SampleRate;
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = x[i];
        Biquad(y, HighShelf(1681.974450955533, 3.999843853973347, 0.7071752369554196, sr));
        Biquad(y, HighPass(38.13547087602444, 0.5003270373238773, sr));
        var prefix = new double[y.Length + 1];
        for (int i = 0; i < y.Length; i++) prefix[i + 1] = prefix[i] + y[i] * y[i];

        var momentary = Blocks(prefix, (int)(0.4 * sr), sr / 10);
        var shortTerm = Blocks(prefix, 3 * sr, sr / 10);
        var mGated = Gate(momentary, 10);
        features["dsp.loudness"] = mGated.Length > 0 ? Lufs(mGated.Average()) : AbsGate;
        if (mGated.Length > 0)
        {
            var l = mGated.Select(Lufs).ToArray();
            double mean = l.Average();
            features["dsp.dynamic_complexity"] = l.Select(v => Math.Abs(v - mean)).Average();
        }
        else features["dsp.dynamic_complexity"] = 0;
        var sGated = Gate(shortTerm, 20).Select(Lufs).OrderBy(v => v).ToArray();
        features["dsp.loudness_range"] = sGated.Length > 1
            ? Signal.Percentile(sGated, 95) - Signal.Percentile(sGated, 10) : 0;
    }

    static double Lufs(double meanSquare) => -0.691 + 10 * Math.Log10(meanSquare + 1e-20);

    // mean squares of windows of `size` samples every `hop`
    static double[] Blocks(double[] prefix, int size, int hop)
    {
        int n = prefix.Length - 1;
        if (n < size) return Array.Empty<double>();
        var z = new double[(n - size) / hop + 1];
        for (int j = 0; j < z.Length; j++) z[j] = (prefix[j * hop + size] - prefix[j * hop]) / size;
        return z;
    }

    // absolute gate, then a relative gate `rel` LU below the loudness of the mean of what passed
    static double[] Gate(double[] z, double rel)
    {
        var a = z.Where(v => Lufs(v) > AbsGate).ToArray();
        if (a.Length == 0) return a;
        double threshold = Lufs(a.Average()) - rel;
        return a.Where(v => Lufs(v) > threshold).ToArray();
    }

    static (double b0, double b1, double b2, double a1, double a2) HighShelf(double f0, double gainDb, double q, int sr)
    {
        double A = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * f0 / sr, cw = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        double sa = 2 * Math.Sqrt(A) * alpha;
        double b0 = A * ((A + 1) + (A - 1) * cw + sa), b1 = -2 * A * ((A - 1) + (A + 1) * cw);
        double b2 = A * ((A + 1) + (A - 1) * cw - sa);
        double a0 = (A + 1) - (A - 1) * cw + sa, a1 = 2 * ((A - 1) - (A + 1) * cw), a2 = (A + 1) - (A - 1) * cw - sa;
        return (b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    static (double b0, double b1, double b2, double a1, double a2) HighPass(double f0, double q, int sr)
    {
        double w = 2 * Math.PI * f0 / sr, cw = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
        return ((1 + cw) / 2 / a0, -(1 + cw) / a0, (1 + cw) / 2 / a0, -2 * cw / a0, (1 - alpha) / a0);
    }

    static void Biquad(double[] y, (double b0, double b1, double b2, double a1, double a2) c)
    {
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < y.Length; i++)
        {
            double x0 = y[i], o = c.b0 * x0 + c.b1 * x1 + c.b2 * x2 - c.a1 * y1 - c.a2 * y2;
            x2 = x1; x1 = x0; y2 = y1; y1 = o;
            y[i] = o;
        }
    }
}
