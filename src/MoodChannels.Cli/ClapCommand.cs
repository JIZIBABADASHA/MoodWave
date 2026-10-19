// moodchannels clap: CLAP embeddings of audio files, one JSON line per track (for checks against the training's
// embeddings and for timing). --windows adds each window's embedding; --list reads "<id>\t<path>" lines.
using System.Diagnostics;
using System.Text.Json;
using MoodChannels;

static class ClapCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: moodchannels clap <clap_audio.onnx> [--segments 6|9|12] [--cpu] [--threads N] [--windows] <audio | --list list.tsv> ...");
            return 2;
        }
        int segments = 12, threads = 0;
        bool gpu = true, windows = false;
        var jobs = new List<(string? id, string path)>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--segments") segments = int.Parse(args[++i]);
            else if (args[i] == "--cpu") gpu = false;
            else if (args[i] == "--threads") threads = int.Parse(args[++i]);
            else if (args[i] == "--windows") windows = true;
            else if (args[i] == "--list")
                jobs.AddRange(File.ReadLines(args[++i]).Select(l => l.Split('\t', 2)).Where(p => p.Length == 2)
                                  .Select(p => ((string?)p[0], p[1])));
            else jobs.Add((null, args[i]));
        }
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var json = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var sw = Stopwatch.StartNew();
        using var clap = new Clap(args[0], gpu, threads);
        Console.Error.WriteLine($"clap: {clap.Device}, model loaded in {sw.ElapsedMilliseconds} ms, {segments} segments");
        // decoding (ffmpeg, its own process) runs one track ahead of the model
        Task<float[]>? next = null;
        for (int k = 0; k < jobs.Count; k++)
        {
            var job = jobs[k];
            var decode = next ?? Task.Run(() => Clap.Decode(job.path));
            if (k + 1 < jobs.Count) { var p = jobs[k + 1].path; next = Task.Run(() => Clap.Decode(p)); } else next = null;
            sw.Restart();
            string line;
            try
            {
                var pcm = decode.GetAwaiter().GetResult();
                double decodeMs = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                var t = clap.EmbedTrack(pcm, segments);
                line = t == null
                    ? JsonSerializer.Serialize(new { id = job.id, file = job.path, error = "shorter than 6 s" }, json)
                    : JsonSerializer.Serialize(new
                    {
                        id = job.id, file = job.path, device = clap.Device, windows = t.Windows, used = t.Used,
                        decode_ms = Math.Round(decodeMs), model_ms = Math.Round(sw.Elapsed.TotalMilliseconds),
                        emb = t.Mean.Select(v => MathF.Round(v, 6)),
                        window_embs = windows ? t.Embeddings.Select(e => e.Select(v => MathF.Round(v, 6))) : null,
                    }, json);
            }
            catch (Exception ex)
            {
                line = JsonSerializer.Serialize(new { id = job.id, file = job.path, error = ex.Message }, json);
            }
            Console.WriteLine(line);
        }
        return 0;
    }
}
