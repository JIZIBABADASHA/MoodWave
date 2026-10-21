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
}
