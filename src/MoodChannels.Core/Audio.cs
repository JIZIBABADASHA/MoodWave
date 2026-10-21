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
    /// ffmpeg is stopped after <see cref="Ffmpeg.Limit"/> (TimeoutException) or when <paramref name="cancel"/> is cancelled.
    /// </summary>
    public static float[] Decode(string path, int maxSeconds = 900, string? ffmpeg = null, bool lowPriority = false, CancellationToken cancel = default)
    {
        var psi = new ProcessStartInfo(Ffmpeg.Path(ffmpeg))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-i", path, "-t", maxSeconds.ToString(), "-map", "0:a:0",
                                  "-ac", "1", "-ar", SampleRate.ToString(), "-f", "f32le", "-" })
            psi.ArgumentList.Add(a);
        using var ms = new MemoryStream();
        var (err, exit) = Ffmpeg.Run(psi, p =>
        {
            var e = p.StandardError.ReadToEndAsync();
            p.StandardOutput.BaseStream.CopyTo(ms);
            return e.Result;
        }, lowPriority, cancel: cancel);
        if (exit != 0) throw new InvalidDataException("ffmpeg failed: " + err.Trim());
        var bytes = ms.GetBuffer();
        var pcm = new float[ms.Length / 4];
        Buffer.BlockCopy(bytes, 0, pcm, 0, pcm.Length * 4);
        return pcm;
    }
}
