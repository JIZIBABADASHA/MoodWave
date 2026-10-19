using System.Diagnostics;

namespace MoodChannels;

/// <summary>Decoding to the engine's input format: 16 kHz mono float32, via ffmpeg.</summary>
public static class Audio
{
    public const int SampleRate = 16000;

    /// <summary>
    /// The first <paramref name="maxSeconds"/> of the first audio stream as 16 kHz mono samples.
    /// <paramref name="ffmpeg"/>: path of ffmpeg.exe (default: the FFMPEG environment variable, else ffmpeg on PATH).
    /// <paramref name="lowPriority"/>: run ffmpeg below normal priority (background analysis next to playback).
    /// </summary>
    public static float[] Decode(string path, int maxSeconds = 900, string? ffmpeg = null, bool lowPriority = false)
    {
        var psi = new ProcessStartInfo(ffmpeg ?? Environment.GetEnvironmentVariable("FFMPEG") ?? "ffmpeg")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-i", path, "-t", maxSeconds.ToString(), "-map", "0:a:0",
                                  "-ac", "1", "-ar", SampleRate.ToString(), "-f", "f32le", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (lowPriority)
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (InvalidOperationException) { }   // already exited
        var err = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidDataException("ffmpeg failed: " + err.Result.Trim());
        var bytes = ms.GetBuffer();
        var pcm = new float[ms.Length / 4];
        Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 4);
        return pcm;
    }
}
