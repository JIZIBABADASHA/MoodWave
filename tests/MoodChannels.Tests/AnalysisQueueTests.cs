using System.Text.Json;
using MoodChannels.Service;
using Xunit;

namespace MoodChannels.Tests;

public class AnalysisQueueTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("moodwave-queue").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_file_analysed_after_the_work_list_was_made_is_not_analysed_again()
    {
        string model = Directory.CreateDirectory(Path.Combine(_dir, "model")).FullName;
        ChannelModelTests.Write(model, weights: true, thresholds: true);
        string file = Path.Combine(_dir, "a.flac");
        File.WriteAllBytes(file, new byte[16]);
        var track = new MoodTrack("a", file, "A", 200, 1, 1);
        var store = new AnalysisStore(_dir);
        store.Load();
        var logs = new List<string>();
        // an ffmpeg that cannot be started: analysing the file again would fail it
        var queue = new AnalysisQueue(store, new Analyzer(model), () => new[] { track }, () => Path.Combine(_dir, "no-ffmpeg.exe"), l => { lock (logs) logs.Add(l); });
        queue.Refresh();                                    // the file still to analyse …
        var done = new Entry { Size = 1, Mtime = 1, Dsp = Dsp.DspFeatures.Version, Ok = true, Duration = 200, At = DateTime.UtcNow };
        store.Put(file, done);                              // … and a worker stores it just after the list was made
        queue.Configure(true, 1);
        var until = DateTime.UtcNow.AddSeconds(20);
        int Pending() => JsonSerializer.SerializeToElement(queue.Status(null, null)).GetProperty("pending").GetInt32();
        while (DateTime.UtcNow < until && Pending() > 0) Thread.Sleep(50);
        Thread.Sleep(200);
        queue.Stop();
        Assert.Equal(0, Pending());
        Assert.DoesNotContain(logs, l => l.StartsWith("analysis failed"));
        Assert.Same(done, store.Get(file));                 // the stored analysis kept
        var status = JsonSerializer.SerializeToElement(queue.Status(null, null));
        Assert.Equal(1, status.GetProperty("analysed").GetInt32());
        store.Dispose();
    }
}
