// Mood and time-of-day channels for audio files.
//
//   moodchannels <model dir> [--features] [--workers N] <audio file | --list list.tsv> ...
//   moodchannels clap <clap_audio.onnx> [--segments 6|9|12] [--cpu] [--threads N] [--windows] <audio | --list list.tsv> ...
//
// One JSON line per track on stdout: channels, BPM, key, probabilities; with --features also every DSP descriptor.
// --list reads "<id>\t<path>" lines and adds "id" to the output. Needs ffmpeg on PATH (or in the FFMPEG variable).
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoodChannels;

if (args.Length > 0 && args[0] == "clap") return ClapCommand.Run(args[1..]);
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: moodchannels <model dir> [--features] [--workers N] <audio | --list list.tsv> ...");
    return 2;
}
bool withFeatures = false;
int workers = 1;
var jobs = new List<(string? id, string path)>();
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--features") withFeatures = true;
    else if (args[i] == "--workers") workers = int.Parse(args[++i]);
    else if (args[i] == "--list")
        jobs.AddRange(File.ReadLines(args[++i]).Select(l => l.Split('\t', 2)).Where(p => p.Length == 2)
                          .Select(p => ((string?)p[0], p[1])));
    else jobs.Add((null, args[i]));
}
Console.OutputEncoding = System.Text.Encoding.UTF8;
var json = new JsonSerializerOptions
{
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};
using var analyzer = new Analyzer(args[0]);
var outLock = new object();
Parallel.ForEach(Partitioner.Create(jobs, EnumerablePartitionerOptions.NoBuffering),
                 new ParallelOptions { MaxDegreeOfParallelism = workers }, job =>
{
    var sw = Stopwatch.StartNew();
    string line;
    try
    {
        var a = analyzer.Analyze(job.path);
        var p = a.Prediction;
        line = JsonSerializer.Serialize(new
        {
            id = job.id,
            file = job.path,
            channels = p.Channels.Select(c => Channels.Names[c]),
            native = p.Channels,
            unclassifiable = p.Unclassifiable,
            bpm = Math.Round(p.Bpm, 2),
            key = a.Key,
            probabilities = Enumerable.Range(0, Channels.Count).ToDictionary(c => Channels.Names[c], c => Math.Round(p.Probabilities[c], 4)),
            p_unclassifiable = Math.Round(p.UnclassifiableProbability, 4),
            model = a.ModelVersion,
            seconds = Math.Round(sw.Elapsed.TotalSeconds, 2),
            features = withFeatures ? a.Features.Values : null,
        }, json);
    }
    catch (Exception e)
    {
        line = JsonSerializer.Serialize(new { id = job.id, file = job.path, error = e.Message }, json);
    }
    lock (outLock) Console.WriteLine(line);
});
return 0;
