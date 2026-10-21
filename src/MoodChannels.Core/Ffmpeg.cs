using System.Diagnostics;

namespace MoodChannels;

/// <summary>Runs ffmpeg with a time limit, and stops it when the caller cancels (the player closing).</summary>
public static class Ffmpeg
{
    /// <summary>Far longer than any track takes (seconds, even at low priority): an ffmpeg this slow is stuck.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    /// <summary>The path of ffmpeg: <paramref name="ffmpeg"/>, else the FFMPEG environment variable, else ffmpeg on PATH.</summary>
    public static string Path(string? ffmpeg) => ffmpeg ?? Environment.GetEnvironmentVariable("FFMPEG") ?? "ffmpeg";

    /// <summary>
    /// Starts ffmpeg, reads what it gives with <paramref name="read"/> and waits for it to end; returns that and its exit
    /// code. It is killed after <paramref name="limit"/> (default <see cref="Limit"/>: TimeoutException) or when
    /// <paramref name="cancel"/> is cancelled (OperationCanceledException).
    /// </summary>
    public static (T Output, int ExitCode) Run<T>(ProcessStartInfo psi, Func<Process, T> read, bool lowPriority = false,
                                                  TimeSpan? limit = null, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        using var p = Process.Start(psi)!;
        if (lowPriority)
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }   // already exited
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stop.CancelAfter(limit ?? Limit);
        // killing it closes its pipes: read and WaitForExit return
        using var kill = stop.Token.Register(() => { try { p.Kill(entireProcessTree: true); } catch (Exception) { } });
        T output = read(p);
        p.WaitForExit();
        cancel.ThrowIfCancellationRequested();
        if (stop.IsCancellationRequested)
        {
            var l = limit ?? Limit;
            throw new TimeoutException($"ffmpeg did not finish in {(l.TotalMinutes >= 1 ? $"{l.TotalMinutes:0} minutes" : $"{l.TotalSeconds:0} s")}");
        }
        return (output, p.ExitCode);
    }
}
