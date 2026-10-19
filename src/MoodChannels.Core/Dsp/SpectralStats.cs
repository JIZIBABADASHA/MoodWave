namespace MoodChannels.Dsp;

/// <summary>
/// Frame-level spectral descriptors on 128 ms Hann frames with a 64 ms hop (16 kHz), each summarised by its mean and
/// standard deviation over the non-silent frames:
///   centroid   : amplitude-weighted mean frequency (Hz)
///   complexity : number of spectral peaks between 100 Hz and 5 kHz within 40 dB of the frame's strongest bin
///   flatness   : 10·log10(geometric / arithmetic mean of the power spectrum), in dB (0 = white noise)
///   flux       : Euclidean distance between consecutive L1-normalised magnitude spectra
///   zcr        : fraction of adjacent sample pairs that change sign
///   dissonance : Sethares' sensory dissonance of the 20 strongest peaks (Plomp–Levelt curve), normalised by
///                the summed peak-pair amplitude products (0 = consonant, ~0.5 = rough)
///   low, high  : share of the spectral energy in 20–150 Hz and in 4–8 kHz
/// </summary>
public sealed class SpectralStats
{
    const int N = 2048, Hop = 1024, Bins = N / 2 + 1, MaxPeaks = 20;
    const double BinHz = (double)Audio.SampleRate / N;
    static readonly string[] Names = { "centroid", "complexity", "flatness", "flux", "zcr", "dissonance", "low", "high" };

    readonly Fft _fft = new(N);
    readonly double[] _win = Fft.Hann(N);
    readonly double[] _mag = new double[Bins], _norm = new double[Bins], _prev = new double[Bins];
    readonly List<(double f, double a)> _peaks = new();

    public void Compute(ReadOnlySpan<float> x, IDictionary<string, double> features)
    {
        var m = Names.ToDictionary(n => n, _ => new Moments());
        int k20 = (int)Math.Ceiling(20 / BinHz), k150 = (int)Math.Floor(150 / BinHz);
        int k100 = (int)Math.Ceiling(100 / BinHz), k5000 = (int)Math.Floor(5000 / BinHz);
        int k4000 = (int)Math.Ceiling(4000 / BinHz);
        bool havePrev = false;
        for (int start = 0; start + N <= x.Length; start += Hop)
        {
            var frame = x.Slice(start, N);
            double energy = 0;
            int crossings = 0;
            for (int i = 0; i < N; i++)
            {
                energy += frame[i] * frame[i];
                if (i > 0 && (frame[i - 1] < 0) != (frame[i] < 0)) crossings++;
            }
            if (energy < 1e-10)                       // digital silence: no meaningful spectrum
            {
                havePrev = false;
                continue;
            }
            _fft.Magnitude(frame, _win, _mag);

            double sumA = 0, sumFA = 0, total = 0, low = 0, high = 0, logSum = 0, maxA = 0;
            for (int k = 1; k < Bins; k++)
            {
                double a = _mag[k], p = a * a;
                sumA += a;
                sumFA += k * BinHz * a;
                total += p;
                logSum += Math.Log(p + 1e-20);
                if (k >= k20 && k <= k150) low += p;
                if (k >= k4000) high += p;
                if (a > maxA) maxA = a;
            }
            if (sumA <= 0) { havePrev = false; continue; }
            m["centroid"].Add(sumFA / sumA);
            m["flatness"].Add(10 * Math.Log10(Math.Exp(logSum / (Bins - 1)) / (total / (Bins - 1)) + 1e-20));
            m["low"].Add(low / total);
            m["high"].Add(high / total);
            m["zcr"].Add(crossings / (double)(N - 1));

            for (int k = 0; k < Bins; k++) _norm[k] = _mag[k] / sumA;
            if (havePrev)
            {
                double d = 0;
                for (int k = 0; k < Bins; k++) d += (_norm[k] - _prev[k]) * (_norm[k] - _prev[k]);
                m["flux"].Add(Math.Sqrt(d));
            }
            Array.Copy(_norm, _prev, Bins);
            havePrev = true;

            // peaks: local maxima in 100 Hz – 5 kHz within 40 dB of the strongest bin, refined by parabolic fit
            _peaks.Clear();
            double floor = maxA * 0.01;
            for (int k = Math.Max(k100, 1); k <= Math.Min(k5000, Bins - 2); k++)
            {
                double b = _mag[k];
                if (b < floor || b <= _mag[k - 1] || b < _mag[k + 1]) continue;
                double a = _mag[k - 1], c = _mag[k + 1], den = a - 2 * b + c;
                double off = den != 0 ? 0.5 * (a - c) / den : 0;
                _peaks.Add(((k + off) * BinHz, b - 0.25 * (a - c) * off));
            }
            m["complexity"].Add(_peaks.Count);
            m["dissonance"].Add(Dissonance(_peaks));
        }
        foreach (var n in Names)
        {
            features[$"dsp.{n}.mean"] = m[n].Mean;
            features[$"dsp.{n}.stdev"] = m[n].Std;
        }
    }

    // Sethares (1993): each pair of partials contributes a_i·a_j·(e^(−3.5·s·Δf) − e^(−5.75·s·Δf)),
    // s = 0.24 / (0.021·f_min + 19), which peaks at about a quarter of the critical bandwidth.
    static double Dissonance(List<(double f, double a)> peaks)
    {
        if (peaks.Count < 2) return 0;
        var p = peaks.OrderByDescending(q => q.a).Take(MaxPeaks).OrderBy(q => q.f).ToArray();
        double d = 0, norm = 0;
        for (int i = 0; i < p.Length; i++)
            for (int j = i + 1; j < p.Length; j++)
            {
                double s = 0.24 / (0.021 * p[i].f + 19), df = p[j].f - p[i].f, w = p[i].a * p[j].a;
                d += w * (Math.Exp(-3.5 * s * df) - Math.Exp(-5.75 * s * df));
                norm += w;
            }
        return norm > 0 ? d / norm : 0;
    }
}
