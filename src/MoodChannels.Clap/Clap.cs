using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MoodChannels;

/// <summary>
/// A track's CLAP audio embedding (LAION-CLAP, music checkpoint, CC0 weights): the mean of the unit embeddings of
/// up to 12 ten-second windows spread evenly over the track, 48 kHz mono. clap_audio.onnx holds the spectrogram front
/// end, so this only decodes, cuts windows and runs the model, one window per run (its batch size is fixed at 1).
/// </summary>
public sealed class Clap : IDisposable
{
    public const int SampleRate = ClapWindows.SampleRate;
    public const int WindowSamples = ClapWindows.WindowSamples;
    public const int Dim = 512;

    readonly InferenceSession _session;
    readonly object _runLock = new();

    /// <summary>What the model runs on: "DirectML" or "CPU".</summary>
    public string Device { get; }

    /// <param name="gpu">Try the GPU (DirectML on Windows); the CPU when it is not there or fails.</param>
    /// <param name="threads">CPU threads for one window (0: ONNX Runtime's choice, every core).</param>
    public Clap(string modelPath, bool gpu = true, int threads = 0)
    {
        if (gpu && OperatingSystem.IsWindows())
        {
            try
            {
                // DirectML wants sequential execution and no memory pattern
                var so = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
                so.AppendExecutionProvider_DML(0);
                _session = new InferenceSession(modelPath, so);
                Device = "DirectML";
                return;
            }
            catch (Exception) { }                            // no DirectX 12 GPU, or the DirectML build is missing
        }
        var cpu = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        if (threads > 0) cpu.IntraOpNumThreads = threads;
        _session = new InferenceSession(modelPath, cpu);
        Device = "CPU";
    }

    /// <summary>The unit embedding of one window (WindowSamples samples, int16-rounded as <see cref="Window"/> cuts it).</summary>
    public float[] Embed(float[] window)
    {
        if (window.Length != WindowSamples) throw new ArgumentException($"a window is {WindowSamples} samples", nameof(window));
        var x = new DenseTensor<float>(window, new[] { 1, WindowSamples });
        lock (_runLock)                                     // one run at a time: the model uses every core (or the GPU)
        {
            using var r = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("waveform", x) });
            return r.First().AsEnumerable<float>().ToArray();
        }
    }

    /// <summary>
    /// The first <paramref name="maxSeconds"/> of the first audio stream as 48 kHz mono samples (ffmpeg as for the
    /// DSP analysis, at CLAP's rate).
    /// </summary>
    public static float[] Decode(string path, string? ffmpeg = null, bool lowPriority = false, int maxSeconds = 900)
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
            pcm = Audio.ReadPcm(p.StandardOutput.BaseStream);
            return e.Result;
        }, lowPriority);
        if (exit != 0) throw new InvalidDataException("ffmpeg failed: " + err.Trim());
        return pcm;
    }

    /// <summary>The window at <paramref name="start"/>, padded with silence to 10 s and rounded to 16-bit steps as CLAP's own examples (and the training data).</summary>
    public static float[] Window(float[] pcm, int start)
    {
        var w = new float[WindowSamples];
        int n = Math.Min(WindowSamples, pcm.Length - start);
        for (int i = 0; i < n; i++)
        {
            float v = Math.Clamp(pcm[start + i], -1f, 1f) * 32767f;
            w[i] = (short)v / 32767f;                       // truncated toward zero, as numpy's astype(int16)
        }
        return w;
    }

    /// <summary>A track: its windows, the embedding of each one asked for, and their mean (not normalised again).</summary>
    public sealed record Track(int Windows, int[] Used, float[][] Embeddings, float[] Mean);

    /// <summary>Embeds a decoded track with the given segment setting; null when it is too short.</summary>
    public Track? EmbedTrack(float[] pcm, int segments)
    {
        var starts = ClapWindows.Starts(pcm.Length);
        if (starts.Length == 0) return null;
        var used = ClapWindows.Pick(starts.Length, segments);
        var embs = EmbedWindows(pcm, used);
        var mean = new float[Dim];
        foreach (var e in embs)
            for (int d = 0; d < Dim; d++) mean[d] += e[d];
        for (int d = 0; d < Dim; d++) mean[d] /= embs.Length;
        return new Track(starts.Length, used, embs, mean);
    }

    /// <summary>The embeddings of these windows of a decoded track (indices as <see cref="ClapWindows.Starts"/> numbers them).</summary>
    public float[][] EmbedWindows(float[] pcm, IReadOnlyList<int> windows)
    {
        var starts = ClapWindows.Starts(pcm.Length);
        return windows.Select(i => Embed(Window(pcm, starts[i]))).ToArray();
    }

    public void Dispose() => _session.Dispose();
}
