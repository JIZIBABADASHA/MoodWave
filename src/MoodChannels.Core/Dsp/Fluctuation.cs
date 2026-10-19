namespace MoodChannels.Dsp;

/// <summary>
/// Detrended fluctuation analysis of the amplitude envelope, after Streich &amp; Herrera, "Detrended Fluctuation
/// Analysis of Music Signals: Danceability Estimation and further Semantic Characterization" (AES 2005).
/// The envelope is the standard deviation of each 10 ms block; its mean-removed running sum is cut into windows of
/// τ = 310 ms … 8.8 s (log-spaced), a straight line is fitted in each window, and F(τ) is the RMS residual.
/// dsp.dfa_alpha is the mean local slope of log F against log τ: low values (strong, regular short-term
/// fluctuation) go with danceable music in the paper.
/// </summary>
public static class Fluctuation
{
    const int Block = Audio.SampleRate / 100;          // 10 ms
    const int MinTau = 31, MaxTau = 880, Scales = 32;

    public static void Compute(ReadOnlySpan<float> x, IDictionary<string, double> features)
    {
        int n = x.Length / Block;
        features["dsp.dfa_alpha"] = double.NaN;
        if (n < 2 * MaxTau) return;
        var env = new double[n];
        for (int b = 0; b < n; b++)
        {
            double s = 0, s2 = 0;
            for (int i = b * Block; i < (b + 1) * Block; i++) { s += x[i]; s2 += x[i] * x[i]; }
            double mean = s / Block;
            env[b] = Math.Sqrt(Math.Max(0, s2 / Block - mean * mean));
        }
        double envMean = env.Average();
        var y = new double[n];
        double acc = 0;
        for (int i = 0; i < n; i++) { acc += env[i] - envMean; y[i] = acc; }

        var taus = Enumerable.Range(0, Scales)
            .Select(k => (int)Math.Round(MinTau * Math.Pow((double)MaxTau / MinTau, k / (Scales - 1.0))))
            .Distinct().ToArray();
        var logF = new double[taus.Length];
        for (int k = 0; k < taus.Length; k++) logF[k] = Math.Log(Fluct(y, taus[k]) + 1e-20);
        double slope = 0;
        for (int k = 1; k < taus.Length; k++)
            slope += (logF[k] - logF[k - 1]) / (Math.Log(taus[k]) - Math.Log(taus[k - 1]));
        features["dsp.dfa_alpha"] = slope / (taus.Length - 1);
    }

    // RMS residual of per-window least-squares lines, over all complete windows of length tau
    static double Fluct(double[] y, int tau)
    {
        int windows = y.Length / tau;
        double sx = 0, sxx = 0;                         // x = 0..tau-1, the same in every window
        for (int i = 0; i < tau; i++) { sx += i; sxx += (double)i * i; }
        double det = tau * sxx - sx * sx, total = 0;
        for (int w = 0; w < windows; w++)
        {
            double sy = 0, sxy = 0, syy = 0;
            for (int i = 0; i < tau; i++)
            {
                double v = y[w * tau + i];
                sy += v; sxy += i * v; syy += v * v;
            }
            double b = (tau * sxy - sx * sy) / det, a = (sy - b * sx) / tau;
            // residual sum of squares of the fitted line, in closed form
            double rss = syy - a * sy - b * sxy;
            total += Math.Max(0, rss);
        }
        return Math.Sqrt(total / (windows * tau));
    }
}
