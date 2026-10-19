namespace MoodChannels.Dsp;

/// <summary>Running mean and population standard deviation (Welford).</summary>
sealed class Moments
{
    long _n;
    double _mean, _m2;

    public void Add(double x)
    {
        _n++;
        double d = x - _mean;
        _mean += d / _n;
        _m2 += d * (x - _mean);
    }

    public double Mean => _n > 0 ? _mean : double.NaN;
    public double Std => _n > 0 ? Math.Sqrt(_m2 / _n) : double.NaN;
}

static class Signal
{
    /// <summary>Centred moving average over 2·<paramref name="half"/> + 1 points (shorter at the edges).</summary>
    public static double[] MovingMean(double[] x, int half)
    {
        var prefix = new double[x.Length + 1];
        for (int i = 0; i < x.Length; i++) prefix[i + 1] = prefix[i] + x[i];
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            int a = Math.Max(0, i - half), b = Math.Min(x.Length, i + half + 1);
            y[i] = (prefix[b] - prefix[a]) / (b - a);
        }
        return y;
    }

    /// <summary>Linear-interpolated percentile (0–100) of an already sorted array.</summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        double pos = p / 100.0 * (sorted.Length - 1);
        int i = (int)Math.Floor(pos);
        if (i >= sorted.Length - 1) return sorted[^1];
        return sorted[i] + (pos - i) * (sorted[i + 1] - sorted[i]);
    }

    public static double Median(IEnumerable<double> x)
    {
        var s = x.OrderBy(v => v).ToArray();
        return Percentile(s, 50);
    }
}
