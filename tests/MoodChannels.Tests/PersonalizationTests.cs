using MoodChannels.Service;
using Xunit;

namespace MoodChannels.Tests;

public class FitTests
{
    const int D = 6;

    static (float[][] Rows, double[] Y) Votes(int n, double spread, int seed)
    {
        // a listener's votes, `spread` scales away from where a built-in model was learnt; descriptor 3 tells them apart
        var rnd = new Random(seed);
        var shift = Enumerable.Range(0, D).Select(_ => (rnd.NextDouble() - 0.5) * spread).ToArray();
        var rows = new float[n][];
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            y[i] = i % 2;
            rows[i] = Enumerable.Range(0, D).Select(j => (float)(shift[j] + (rnd.NextDouble() - 0.5) * spread)).ToArray();
            rows[i][3] = (float)(y[i] > 0 ? spread : -spread);
        }
        return (rows, y);
    }

    static Personalization.Model Prior() => new()
    {
        Fill = new double[D], Mean = new double[D], Scale = Enumerable.Repeat(1.0, D).ToArray(),
        W = new[] { 0.5, -0.4, 0.3, 0.1, -0.5, 0.2 }, B = 0.1,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(1000)]
    [InlineData(100000)]
    public void Weights_stay_finite_and_the_votes_are_learnt(double spread)
    {
        // far from a built-in model's data its probabilities are all 0 or 1: the full Newton step used to end in NaN
        for (int seed = 0; seed < 10; seed++)
        {
            var (rows, y) = Votes(30, spread, seed);
            foreach (var prior in new[] { Prior(), null })
            {
                var m = Personalization.Fit(rows, y, prior);
                Assert.All(m.W, w => Assert.True(double.IsFinite(w)));
                Assert.True(double.IsFinite(m.B));
                if (spread >= 10)                           // separable by far more than the pull towards the prior
                    for (int i = 0; i < rows.Length; i++) Assert.Equal(y[i] > 0, m.NotBelong(rows[i]) >= 0.5);
            }
        }
    }
}

public class VersionTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("moodwave-test").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Training_without_enough_votes_changes_no_channel()
    {
        // a vote below MinEach changes only that track (MoodService drops its adjustment), not the whole channel
        var store = new AnalysisStore(_dir);
        store.Load();
        var p = new Personalization(store);
        var before = p.Versions();
        store.SetVote("/a.flac", 6, -1);
        p.Train(6);
        Assert.Equal(before, p.Versions());
        store.Dispose();
    }

    [Fact]
    public void Switching_the_built_in_tuning_changes_its_channels_only()
    {
        var store = new AnalysisStore(_dir);
        store.Load();
        var p = new Personalization(store);
        string tuning = Path.Combine(AppContext.BaseDirectory, "tuning.json");
        System.Text.Json.Nodes.JsonArray Of(double v) => new(store.FeatureNames.Select(_ => (System.Text.Json.Nodes.JsonNode)v).ToArray());
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["format"] = 1, ["dsp"] = MoodChannels.Dsp.DspFeatures.Version,
            ["features"] = new System.Text.Json.Nodes.JsonArray(store.FeatureNames.Select(f => (System.Text.Json.Nodes.JsonNode)f).ToArray()),
            ["channels"] = new System.Text.Json.Nodes.JsonObject
            {
                ["6"] = new System.Text.Json.Nodes.JsonObject { ["fill"] = Of(0), ["mean"] = Of(0), ["scale"] = Of(1), ["w"] = Of(0), ["b"] = 0 },
            },
        };
        File.WriteAllText(tuning, json.ToJsonString());
        Assert.StartsWith("built-in tuning: Relax", p.LoadBuiltIn(tuning, store.FeatureNames));
        var before = p.Versions();
        p.UseBuiltIn = false;
        var after = p.Versions();
        for (int ch = 0; ch < Channels.Count; ch++)
            if (ch == 6) Assert.NotEqual(before[ch], after[ch]); else Assert.Equal(before[ch], after[ch]);
        store.Dispose();
    }
}
