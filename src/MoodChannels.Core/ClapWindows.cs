namespace MoodChannels;

/// <summary>
/// Where the CLAP embedding (advanced AI) takes a track's ten-second windows from, and which of them a segment setting
/// uses. Plain arithmetic, without the model: the service decides with it what a track still needs, the CLAP process
/// (MoodChannels.Clap) cuts the windows with it.
/// </summary>
public static class ClapWindows
{
    public const int SampleRate = 48000;
    public const int WindowSamples = 10 * SampleRate;
    public const int MaxWindows = 12;
    public const double MinSeconds = 6;

    /// <summary>The segment settings offered: 6, 9 or 12 windows per track.</summary>
    public static readonly int[] SegmentChoices = { 6, 9, 12 };

    /// <summary>The nearest offered setting to <paramref name="segments"/>.</summary>
    public static int Segments(int segments) => segments >= 12 ? 12 : segments >= 9 ? 9 : 6;

    /// <summary>How many windows a track of <paramref name="samples"/> samples (48 kHz) has: one per full 10 s, 1 to 12; 0 under <see cref="MinSeconds"/>.</summary>
    public static int Count(long samples) =>
        samples < MinSeconds * SampleRate ? 0 : (int)Math.Min(MaxWindows, Math.Max(1, samples / WindowSamples));

    /// <summary>
    /// Where a track's windows start: one per full 10 s, at most 12, spread evenly from the beginning to the end (as
    /// numpy's linspace(0, n − window, count) truncated to whole samples, which the training data used). Empty when
    /// the track is shorter than <see cref="MinSeconds"/>; a track under 10 s has one window (padded with silence).
    /// </summary>
    public static int[] Starts(int samples)
    {
        int n = Count(samples);
        if (n == 0) return Array.Empty<int>();
        int last = Math.Max(0, samples - WindowSamples);
        if (n == 1) return new[] { 0 };
        double step = (double)last / (n - 1);
        var s = new int[n];
        for (int i = 0; i < n - 1; i++) s[i] = (int)(i * step);
        s[n - 1] = last;
        return s;
    }

    /// <summary>
    /// Which of a track's windows the segment setting uses. With all 12, the settings nest (6 ⊂ 9 ⊂ 12): 12 is every
    /// window, 9 leaves out windows 1, 5 and 9, 6 takes the even ones, so raising the setting only adds windows. A
    /// shorter track uses all its windows, or as many spread evenly when it has more than the setting.
    /// </summary>
    public static int[] Pick(int windows, int segments)
    {
        if (windows <= 0) return Array.Empty<int>();
        if (windows == MaxWindows)
            return segments >= 12 ? Enumerable.Range(0, 12).ToArray()
                 : segments >= 9 ? new[] { 0, 2, 3, 4, 6, 7, 8, 10, 11 }
                 : new[] { 0, 2, 4, 6, 8, 10 };
        if (segments >= windows) return Enumerable.Range(0, windows).ToArray();
        return Enumerable.Range(0, segments).Select(j => (int)Math.Round((j + 0.5) * windows / segments - 0.5)).Distinct().ToArray();
    }

    /// <summary>The windows as a bit mask (bit i = window i).</summary>
    public static int Mask(IEnumerable<int> windows) => windows.Aggregate(0, (m, i) => m | 1 << i);

    /// <summary>The windows of a bit mask, in order.</summary>
    public static int[] Windows(int mask) => Enumerable.Range(0, MaxWindows).Where(i => (mask & 1 << i) != 0).ToArray();
}
