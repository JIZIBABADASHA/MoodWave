namespace MoodChannels.Dsp;

/// <summary>
/// Key and mode by profile matching. A chromagram is built from spectral peaks (256 ms Hann frames, 128 ms hop,
/// 80 Hz – 5 kHz, within 60 dB of the frame maximum): each peak adds its amplitude to its nearest pitch class
/// (A4 = 440 Hz), weighted by cos²(π·d) for a distance of d semitones from that pitch. Frames are normalised to a
/// maximum of 1 and averaged. The mean chroma is correlated (Pearson) with the 24 rotations of the Krumhansl–Kessler
/// major and minor key profiles (Krumhansl &amp; Kessler 1982; Krumhansl, "Cognitive Foundations of Musical Pitch", 1990).
///   dsp.key_major    : 1 if the best key is major
///   dsp.key_strength : its correlation (−1 … 1)
///   dsp.key_clarity  : best minus second-best correlation over all 24 keys
///   dsp.mode_contrast: best major minus best minor correlation (&gt; 0 leans major)
/// </summary>
public sealed class KeyDetector
{
    const int N = 4096, Hop = 2048, Bins = N / 2 + 1;
    const double BinHz = (double)Audio.SampleRate / N;
    static readonly double[] Major = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    static readonly double[] Minor = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
    public static readonly string[] PitchNames = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };

    readonly Fft _fft = new(N);
    readonly double[] _win = Fft.Hann(N);
    readonly double[] _mag = new double[Bins];

    /// <summary>Adds the key features; returns the key name, e.g. "F# minor" (empty if undetermined).</summary>
    public string Compute(ReadOnlySpan<float> x, IDictionary<string, double> features)
    {
        features["dsp.key_major"] = double.NaN;
        features["dsp.key_strength"] = double.NaN;
        features["dsp.key_clarity"] = double.NaN;
        features["dsp.mode_contrast"] = double.NaN;
        var chroma = new double[12];
        var frame = new double[12];
        int used = 0;
        int kLo = Math.Max(1, (int)Math.Ceiling(80 / BinHz)), kHi = Math.Min(Bins - 2, (int)Math.Floor(5000 / BinHz));
        for (int start = 0; start + N <= x.Length; start += Hop)
        {
            _fft.Magnitude(x.Slice(start, N), _win, _mag);
            double max = 0;
            for (int k = kLo; k <= kHi; k++) max = Math.Max(max, _mag[k]);
            if (max < 1e-6) continue;
            Array.Clear(frame);
            for (int k = kLo; k <= kHi; k++)
            {
                double b = _mag[k];
                if (b < max * 1e-3 || b <= _mag[k - 1] || b < _mag[k + 1]) continue;
                double a = _mag[k - 1], c = _mag[k + 1], den = a - 2 * b + c;
                double off = den != 0 ? 0.5 * (a - c) / den : 0;
                double f = (k + off) * BinHz, amp = b - 0.25 * (a - c) * off;
                double semis = 12 * Math.Log2(f / 440.0) + 9;          // 0 = C
                double nearest = Math.Round(semis), d = semis - nearest;
                int pc = (int)(((long)nearest % 12 + 12) % 12);
                double w = Math.Cos(Math.PI * d);
                frame[pc] += amp * w * w;
            }
            double fm = frame.Max();
            if (fm <= 0) continue;
            for (int i = 0; i < 12; i++) chroma[i] += frame[i] / fm;
            used++;
        }
        if (used == 0) return "";
        double bestR = double.NegativeInfinity, secondR = double.NegativeInfinity;
        var bestOfMode = new[] { double.NegativeInfinity, double.NegativeInfinity };
        int bestKey = 0;
        bool bestMajor = true;
        for (int mode = 0; mode < 2; mode++)
            for (int tonic = 0; tonic < 12; tonic++)
            {
                var prof = mode == 0 ? Major : Minor;
                double r = Pearson(chroma, i => prof[((i - tonic) % 12 + 12) % 12]);
                bestOfMode[mode] = Math.Max(bestOfMode[mode], r);
                if (r > bestR) { secondR = bestR; bestR = r; bestKey = tonic; bestMajor = mode == 0; }
                else if (r > secondR) secondR = r;
            }
        features["dsp.key_major"] = bestMajor ? 1 : 0;
        features["dsp.key_strength"] = bestR;
        features["dsp.key_clarity"] = bestR - secondR;
        features["dsp.mode_contrast"] = bestOfMode[0] - bestOfMode[1];
        return $"{PitchNames[bestKey]} {(bestMajor ? "major" : "minor")}";
    }

    static double Pearson(double[] x, Func<int, double> y)
    {
        double mx = x.Average(), my = 0;
        for (int i = 0; i < 12; i++) my += y(i);
        my /= 12;
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < 12; i++)
        {
            double a = x[i] - mx, b = y(i) - my;
            sxy += a * b; sxx += a * a; syy += b * b;
        }
        return sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : 0;
    }
}
