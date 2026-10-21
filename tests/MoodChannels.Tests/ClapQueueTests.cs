using System.Text.Json;
using MoodChannels.Service;
using Xunit;

namespace MoodChannels.Tests;

public class ClapQueueTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("moodwave-clap").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static Entry Analysed(MoodTrack t) => new()
    {
        Size = t.Size, Mtime = t.Mtime, Dsp = MoodChannels.Dsp.DspFeatures.Version, Ok = true, Duration = 200, At = DateTime.UtcNow,
    };

    static int Total(ClapQueue q) => JsonSerializer.SerializeToElement(q.Status()).GetProperty("total").GetInt32();

    [Fact]
    public void A_file_counted_by_a_refresh_is_not_counted_again_when_its_analysis_reports_in()
    {
        // the DSP analysis stores a file, measures its loudness (seconds), then reports it (Fresh): a refresh in between
        // has counted it already
        var store = new AnalysisStore(_dir);
        store.Load();
        string model = Path.Combine(_dir, "clap.onnx");
        File.WriteAllText(model, "not a model: only hashed for its version");
        var a = new MoodTrack("a", "/m/a.flac", "A", 200, 100, 1);
        var b = new MoodTrack("b", "/m/b.flac", "A", 200, 200, 1);
        var q = new ClapQueue(store, () => new[] { a, b }, () => null, () => false, () => model, _dir, _ => { });
        store.Put(a.Path, Analysed(a));
        q.Configure(true, 12, gpu: false);                  // the work list: a
        Assert.Equal(1, Total(q));
        store.Put(b.Path, Analysed(b));                     // b stored …
        q.Refresh();                                        // … a refresh counts it …
        Assert.Equal(2, Total(q));
        q.Fresh(b);                                         // … and then the analysis reports it
        Assert.Equal(2, Total(q));
        q.Stop();
        store.Dispose();
    }

    [Fact]
    public void A_file_the_refresh_did_not_see_is_counted_when_it_reports_in()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        string model = Path.Combine(_dir, "clap.onnx");
        File.WriteAllText(model, "not a model");
        var a = new MoodTrack("a", "/m/a.flac", "A", 200, 100, 1);
        var b = new MoodTrack("b", "/m/b.flac", "A", 200, 200, 1);
        var q = new ClapQueue(store, () => new[] { a, b }, () => null, () => false, () => model, _dir, _ => { });
        store.Put(a.Path, Analysed(a));
        q.Configure(true, 12, gpu: false);
        store.Put(b.Path, Analysed(b));
        q.Fresh(b);                                         // no refresh since: b is new to the queue
        Assert.Equal(2, Total(q));
        q.Stop();
        store.Dispose();
    }
}
