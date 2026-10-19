namespace MoodChannels;

/// <summary>Radix-2 complex FFT of a fixed power-of-two size, double precision. Holds its own buffers: not thread-safe.</summary>
public sealed class Fft
{
    public int Size { get; }
    readonly double[] _cos, _sin, _re, _im;
    readonly int[] _rev;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two", nameof(size));
        Size = size;
        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (int k = 0; k < size / 2; k++)
        {
            _cos[k] = Math.Cos(-2.0 * Math.PI * k / size);
            _sin[k] = Math.Sin(-2.0 * Math.PI * k / size);
        }
        _rev = new int[size];
        int bits = (int)Math.Log2(size);
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _rev[i] = r;
        }
        _re = new double[size];
        _im = new double[size];
    }

    /// <summary>Periodic Hann window of length <paramref name="n"/>.</summary>
    public static double[] Hann(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / n);
        return w;
    }

    /// <summary>
    /// Magnitude spectrum (Size/2 + 1 bins) of <c>frame[i] * window[i]</c>; samples past the end of
    /// <paramref name="frame"/> count as zero.
    /// </summary>
    public void Magnitude(ReadOnlySpan<float> frame, double[] window, Span<double> mag)
    {
        int n = Size;
        for (int i = 0; i < n; i++)
        {
            _re[_rev[i]] = i < frame.Length ? frame[i] * window[i] : 0.0;
            _im[_rev[i]] = 0.0;
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len / 2, stride = n / len;
            for (int i = 0; i < n; i += len)
                for (int k = 0; k < half; k++)
                {
                    double wr = _cos[k * stride], wi = _sin[k * stride];
                    int a = i + k, b = a + half;
                    double tr = _re[b] * wr - _im[b] * wi, ti = _re[b] * wi + _im[b] * wr;
                    _re[b] = _re[a] - tr; _im[b] = _im[a] - ti;
                    _re[a] += tr; _im[a] += ti;
                }
        }
        for (int k = 0; k <= n / 2; k++) mag[k] = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
    }
}
