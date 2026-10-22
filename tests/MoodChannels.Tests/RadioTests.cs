using System.Reflection;
using MoodChannels.Service;
using Xunit;

namespace MoodChannels.Tests;

public class RadioTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("moodwave-radio").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Forgetting_the_AI_data_empties_the_radio_until_embeddings_come_again()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        var radio = new Radio(store, () => "m", _ => 120, _ => { });
        float[] Vec(int seed) { var r = new Random(seed); return Enumerable.Range(0, AnalysisStore.ClapDim).Select(_ => (float)r.NextDouble() - 0.5f).ToArray(); }
        for (int i = 0; i < 3; i++) store.AddClap($"/m/{i}.flac", 1, 1, "m", 1, new List<(int, float[])> { (0, Vec(i)) });
        radio.Ensure();
        Assert.Equal(3, radio.Count);

        store.ForgetClap();                                 // the listener deleted the AI data (ai.forget / ai.delete)
        radio.Forget();
        radio.Ensure();
        Assert.Equal(0, radio.Count);
        Assert.False(radio.Has("/m/0.flac"));

        store.AddClap("/m/0.flac", 1, 1, "m", 1, new List<(int, float[])> { (0, Vec(0)) });   // worked out again
        radio.Ensure();
        Assert.Equal(1, radio.Count);
        store.Dispose();
    }

    static float[] Vec(int seed) { var r = new Random(seed); return Enumerable.Range(0, AnalysisStore.ClapDim).Select(_ => (float)r.NextDouble() - 0.5f).ToArray(); }

    /// <summary>
    /// Holds the store busy (its lock taken on another thread) and runs <paramref name="ensure"/> on a third, until it
    /// waits for the store inside Ensure; <paramref name="meanwhile"/> then runs, and the store is let go.
    /// </summary>
    static void WhileEnsureReads(AnalysisStore store, Action ensure, Action meanwhile)
    {
        var lk = typeof(AnalysisStore).GetField("_lock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (lk) { held.Set(); release.Wait(); } });
        holder.Start();
        held.Wait();
        var t = new Thread(() => ensure());
        t.Start();
        var until = DateTime.UtcNow.AddSeconds(10);
        while (t.ThreadState != ThreadState.WaitSleepJoin && DateTime.UtcNow < until) Thread.Sleep(5);
        Thread.Sleep(50);
        meanwhile();
        release.Set();
        holder.Join();
        t.Join();
    }

    [Fact]
    public void A_forget_while_the_radio_reads_the_store_is_not_undone()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        var radio = new Radio(store, () => "m", _ => 120, _ => { });
        for (int i = 0; i < 3; i++) store.AddClap($"/m/{i}.flac", 1, 1, "m", 1, new List<(int, float[])> { (0, Vec(i)) });
        // the AI data deleted while the radio reads it: the store's rows are gone, the radio must not keep what it read
        WhileEnsureReads(store, radio.Ensure, () => radio.Forget());
        store.ForgetClap();
        Assert.Equal(0, radio.Count);
        radio.Ensure();                                     // read again: the store is empty now
        Assert.Equal(0, radio.Count);
        store.Dispose();
    }

    [Fact]
    public void An_embedding_stored_while_the_radio_reads_the_store_is_not_lost()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        var radio = new Radio(store, () => "m", _ => 120, _ => { });
        store.AddClap("/m/0.flac", 1, 1, "m", 1, new List<(int, float[])> { (0, Vec(0)) });
        var add = typeof(Radio).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var e = new ClapEntry { Path = "/m/new.flac", Model = "m" };
        // stored just after the radio's read began (the store's ClapAdded): not in what it read
        WhileEnsureReads(store, radio.Ensure, () => add.Invoke(radio, new object[] { e, Vec(9) }));
        Assert.True(radio.Has("/m/0.flac"));
        Assert.True(radio.Has("/m/new.flac"));
        store.Dispose();
    }

    [Fact]
    public void A_tempo_is_worked_out_again_for_a_file_analysed_again()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        double tempo = 100;
        int asked = 0;
        var radio = new Radio(store, () => "m", _ => { asked++; return tempo; }, _ => { });
        var bpm = typeof(Radio).GetMethod("Bpm", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var t = new MoodTrack("a", "/m/a.flac", "A", 200, 1, 1);
        Entry Analysed(long mtime) => new() { Size = 1, Mtime = mtime, Dsp = Dsp.DspFeatures.Version, Ok = true, Duration = 200, At = DateTime.UtcNow };
        store.Put(t.Path, Analysed(1));
        Assert.Equal(100.0, (double)bpm.Invoke(radio, new object[] { t })!);
        Assert.Equal(100.0, (double)bpm.Invoke(radio, new object[] { t })!);
        Assert.Equal(1, asked);                             // kept
        tempo = 140;
        store.Put(t.Path, Analysed(2));                     // the file changed and was analysed again
        Assert.Equal(140.0, (double)bpm.Invoke(radio, new object[] { t })!);
        store.Dispose();
    }
}
