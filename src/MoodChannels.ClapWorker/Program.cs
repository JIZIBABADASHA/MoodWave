// moodwave-clap: CLAP embeddings for MoodService (ClapClient), one JSON object per line on stdin / stdout. Decoding
// (ffmpeg) runs one track ahead of the model. stdout carries the protocol only; anything else goes to stderr.
//
//   in   {"op":"init", "model", "gpu", "threads", "ffmpeg"}         once, first
//        {"op":"embed", "id":n, "path", "segments", "have":mask}    the windows of the segment setting not in have
//        (the end of stdin: quit)
//   out  {"ready":true, "device"} | {"ready":false, "error"}         after init
//        {"id":n, "windows", "embs":[[window, base64 float32 × 512], …], "decode_ms", "model_ms"}
//        {"id":n, "windows":0}                                       shorter than 6 s: no embedding
//        {"id":n, "error"}
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoodChannels;

try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }

var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };
var sendLock = new object();
void Send(object message)
{
    string line = JsonSerializer.Serialize(message);
    lock (sendLock)
    {
        stdout.Write(line);
        stdout.Write('\n');
        stdout.Flush();
    }
}

var inbox = new BlockingCollection<JsonNode>();
// stdin is read on a thread of its own: when it ends (the player stopped or crashed) the process ends at once, even
// in the middle of a track
new Thread(() =>
{
    var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    string? line;
    while ((line = stdin.ReadLine()) != null)
    {
        if (line.Length == 0) continue;
        try { inbox.Add(JsonNode.Parse(line)!); }
        catch (Exception ex) { Console.Error.WriteLine("bad message: " + ex.Message); }
    }
    Environment.Exit(0);
}) { IsBackground = true, Name = "stdin" }.Start();

Clap? clap = null;
string? ffmpeg = null;
// decoded tracks, at most one waiting for the model
var decoded = new BlockingCollection<(JsonNode Job, float[]? Pcm, string? Error, double Ms)>(1);
new Thread(() =>
{
    foreach (var m in inbox.GetConsumingEnumerable())
    {
        switch ((string?)m["op"])
        {
            case "init" when clap == null:
                try
                {
                    ffmpeg = (string?)m["ffmpeg"];
                    clap = new Clap((string)m["model"]!, (bool?)m["gpu"] ?? true, (int?)m["threads"] ?? 0);
                    Send(new { ready = true, device = clap.Device });
                }
                catch (Exception ex) { Send(new { ready = false, error = ex.Message }); }
                break;
            case "embed":
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var pcm = Clap.Decode((string)m["path"]!, string.IsNullOrEmpty(ffmpeg) ? null : ffmpeg, lowPriority: true);
                    decoded.Add((m, pcm, null, sw.Elapsed.TotalMilliseconds));
                }
                catch (Exception ex) { decoded.Add((m, null, ex.Message, 0)); }
                break;
            }
        }
    }
}) { IsBackground = true, Name = "decode" }.Start();

foreach (var (job, pcm, error, decodeMs) in decoded.GetConsumingEnumerable())
{
    var id = job["id"]!.DeepClone();
    if (clap == null) { Send(new { id, error = "not initialised" }); continue; }
    if (pcm == null) { Send(new { id, error }); continue; }
    try
    {
        int windows = ClapWindows.Count(pcm.Length);
        if (windows == 0) { Send(new { id, windows }); continue; }
        int have = (int?)job["have"] ?? 0;
        var want = ClapWindows.Pick(windows, ClapWindows.Segments((int?)job["segments"] ?? 12)).Where(i => (have & 1 << i) == 0).ToArray();
        var sw = Stopwatch.StartNew();
        var embs = clap.EmbedWindows(pcm, want);
        Send(new
        {
            id, windows,
            embs = want.Select((w, k) => new object[] { w, Convert.ToBase64String(MemoryMarshal.AsBytes(embs[k].AsSpan())) }),
            decode_ms = Math.Round(decodeMs), model_ms = Math.Round(sw.Elapsed.TotalMilliseconds),
        });
    }
    catch (Exception ex) { Send(new { id, error = ex.Message }); }
}
