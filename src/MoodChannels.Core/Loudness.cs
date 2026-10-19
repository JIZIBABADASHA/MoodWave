using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MoodChannels;

/// <summary>
/// A track's loudness for level matching (ITU-R BS.1770 / EBU R128), measured at the file's own quality by ffmpeg's
/// ebur128 filter: the integrated loudness and the true peak (4× oversampled), as ReplayGain 2 uses them.
/// </summary>
public static class Loudness
{
    /// <summary>Stored with each measurement: a different version is measured again.</summary>
    public const int Version = 1;

    /// <summary>ReplayGain 2's reference level: gain = Reference − integrated loudness.</summary>
    public const double Reference = -18;

    /// <param name="Lufs">Integrated loudness (LUFS).</param>
    /// <param name="Peak">True peak, linear (1.0 = full scale).</param>
    public readonly record struct Result(double Lufs, double Peak);

    static readonly Regex Integrated = new(@"I:\s+(-?[\d.]+)\s+LUFS", RegexOptions.CultureInvariant);
    static readonly Regex TruePeak = new(@"Peak:\s+(-?[\d.]+|-inf)\s+dBFS", RegexOptions.CultureInvariant);

    /// <summary>
    /// Measures the first audio stream of a file; null when it has no level to match (silence: below the gate's
    /// -70 LUFS). Throws when ffmpeg cannot read it. <paramref name="ffmpeg"/> and <paramref name="lowPriority"/> as
    /// in <see cref="Audio.Decode"/>.
    /// </summary>
    public static Result? Measure(string path, string? ffmpeg = null, bool lowPriority = false)
    {
        var psi = new ProcessStartInfo(ffmpeg ?? Environment.GetEnvironmentVariable("FFMPEG") ?? "ffmpeg")
        {
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        // framelog=verbose: the per-frame lines below the log level, so only the summary is printed
        foreach (var a in new[] { "-hide_banner", "-nostats", "-nostdin", "-i", path, "-map", "0:a:0", "-vn",
                                  "-af", "ebur128=peak=true:framelog=verbose", "-f", "null", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (lowPriority)
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }   // already exited
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidDataException("ffmpeg failed: " + err.Trim());
        int at = err.LastIndexOf("Summary:", StringComparison.Ordinal);
        string summary = at < 0 ? "" : err[at..];
        var i = Integrated.Match(summary);
        var tp = TruePeak.Match(summary);
        if (!i.Success) throw new InvalidDataException("ffmpeg gave no loudness");
        double lufs = double.Parse(i.Groups[1].Value, CultureInfo.InvariantCulture);
        if (lufs <= -69.9) return null;
        double peakDb = tp.Success && tp.Groups[1].Value != "-inf" ? double.Parse(tp.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        return new Result(lufs, Math.Pow(10, peakDb / 20));
    }
}
