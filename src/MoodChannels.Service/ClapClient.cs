using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MoodChannels.Service;

/// <summary>
/// The CLAP process (moodwave-clap, src/MoodChannels.ClapWorker): starts it, sends it tracks and gets their window
/// embeddings back, in JSON lines. It is a child of the process this runs in (MIKU on Windows, moodwave-helper on
/// macOS), runs at low priority and ends by itself when its stdin closes, also when this process dies.
/// </summary>
sealed class ClapClient : IDisposable
{
    /// <summary>What came back for a track: its windows (0: too short) and the new windows' embeddings, or an error.</summary>
    public sealed record Result(int Windows, List<(int Window, float[] Embedding)> Embeddings, string? Error, double DecodeMs, double ModelMs);

    readonly Process _p;
    readonly object _lock = new();
    readonly Dictionary<long, TaskCompletionSource<Result>> _waiting = new();
    readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long _next;
    bool _exited;

    /// <summary>The process ended (crashed, or was stopped); every track waiting got an error.</summary>
    public event Action<ClapClient>? Exited;

    /// <summary>Where the model runs ("DirectML", "CPU"), once ready.</summary>
    public string? Device { get; private set; }

    ClapClient(string exe, Action<string> log)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        _p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _p.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
        _p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log("clap: " + e.Data); };
        _p.Exited += (_, _) => OnExit();
        _p.Start();
        try { _p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }   // it lowers itself too
        _p.BeginOutputReadLine();
        _p.BeginErrorReadLine();
    }

    /// <summary>Starts the process and loads the model; fails with the reason when it cannot.</summary>
    public static async Task<ClapClient> StartAsync(string exe, string model, bool gpu, int threads, string? ffmpeg, Action<string> log)
    {
        var c = new ClapClient(exe, log);
        try
        {
            c.Send(new { op = "init", model, gpu, threads, ffmpeg });
            var done = await Task.WhenAny(c._ready.Task, Task.Delay(TimeSpan.FromMinutes(2)));
            if (done != c._ready.Task) throw new TimeoutException("the CLAP process did not load the model in 2 minutes");
            c.Device = await c._ready.Task;
            return c;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    void Send(object message)
    {
        string line = JsonSerializer.Serialize(message);
        lock (_lock)
        {
            if (_exited) throw new InvalidOperationException("the CLAP process has ended");
            _p.StandardInput.Write(line);
            _p.StandardInput.Write('\n');
            _p.StandardInput.Flush();
        }
    }

    /// <summary>The embeddings of the windows a track's segment setting uses that are not in <paramref name="have"/> (a mask).</summary>
    public Task<Result> Embed(string path, int segments, int have)
    {
        var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        long id;
        lock (_lock)
        {
            if (_exited) { tcs.SetException(new InvalidOperationException("the CLAP process has ended")); return tcs.Task; }
            id = ++_next;
            _waiting[id] = tcs;
        }
        try { Send(new { op = "embed", id, path, segments, have }); }
        catch (Exception ex) { lock (_lock) _waiting.Remove(id); tcs.TrySetException(ex); }
        return tcs.Task;
    }

    void OnLine(string line)
    {
        JsonNode? m;
        try { m = JsonNode.Parse(line); } catch (Exception) { return; }
        if (m == null) return;
        if (m["ready"] is JsonNode ready)
        {
            if ((bool)ready) _ready.TrySetResult((string?)m["device"] ?? "CPU");
            else _ready.TrySetException(new InvalidOperationException((string?)m["error"] ?? "the model did not load"));
            return;
        }
        if (m["id"] is not JsonNode idNode) return;
        long id = (long)idNode;
        TaskCompletionSource<Result>? tcs;
        lock (_lock) if (_waiting.Remove(id, out tcs) == false) return;
        if ((string?)m["error"] is string error) { tcs!.TrySetResult(new Result(0, new(), error, 0, 0)); return; }
        var embs = new List<(int, float[])>();
        if (m["embs"] is JsonArray list)
            foreach (var x in list)
                embs.Add(((int)x![0]!, MemoryMarshal.Cast<byte, float>(Convert.FromBase64String((string)x[1]!)).ToArray()));
        tcs!.TrySetResult(new Result((int?)m["windows"] ?? 0, embs, null, (double?)m["decode_ms"] ?? 0, (double?)m["model_ms"] ?? 0));
    }

    void OnExit()
    {
        List<TaskCompletionSource<Result>> waiting;
        lock (_lock)
        {
            if (_exited) return;
            _exited = true;
            waiting = _waiting.Values.ToList();
            _waiting.Clear();
        }
        string why = "the CLAP process ended" + (_p.HasExited ? $" (exit {_p.ExitCode})" : "");
        _ready.TrySetException(new InvalidOperationException(why));
        foreach (var t in waiting) t.TrySetException(new InvalidOperationException(why));
        Exited?.Invoke(this);
    }

    /// <summary>Stops the process: closing its stdin ends it; it is killed if it has not ended a few seconds later.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (!_exited) try { _p.StandardInput.Close(); } catch (Exception) { }
        }
        try { if (!_p.WaitForExit(3000)) _p.Kill(); } catch (Exception) { }
        OnExit();
        _p.Dispose();
    }
}
