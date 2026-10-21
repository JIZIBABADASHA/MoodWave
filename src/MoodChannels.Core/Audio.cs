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
        float[] pcm = Array.Empty<float>();
        var (err, exit) = Ffmpeg.Run(psi, p =>
        {
            var e = p.StandardError.ReadToEndAsync();
            pcm = ReadPcm(p.StandardOutput.BaseStream);
            return e.Result;
        }, lowPriority, cancel: cancel);
        if (exit != 0) throw new InvalidDataException("ffmpeg failed: " + err.Trim());
        return pcm;
    }

    /// <summary>
    /// float32 samples (f32le) from a stream, to its end. Read in small blocks and put together once at the end: a
    /// growing buffer (MemoryStream) holds up to twice the track while it grows, and a copy of it a third time.
    /// </summary>
    public static float[] ReadPcm(Stream input)
    {
        const int Block = 64 << 10;                         // under the large object heap's 85 000 bytes
        var blocks = new List<byte[]>();
        long total = 0;
        while (true)
        {
            var b = new byte[Block];
            int n = 0, r;
            while (n < Block && (r = input.Read(b, n, Block - n)) > 0) n += r;
            if (n > 0) blocks.Add(n == Block ? b : b[..n]);
            total += n;
            if (n < Block) break;
        }
        var pcm = new float[total / 4];
        int at = 0, bytes = pcm.Length * 4;
        foreach (var b in blocks)
        {
            int len = Math.Min(b.Length, bytes - at);       // a last odd byte or three: not a whole sample
            if (len > 0) Buffer.BlockCopy(b, 0, pcm, at, len);
            at += len;
        }
        return pcm;
    }
}
