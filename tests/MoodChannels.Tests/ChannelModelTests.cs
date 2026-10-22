using System.Text.Json.Nodes;
using Xunit;

namespace MoodChannels.Tests;

public class ChannelModelTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("moodwave-model").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A model directory: every model a tree file without trees (each class its base margin only).</summary>
    internal static void Write(string dir, bool weights, bool thresholds)
    {
        void Trees(string name, bool softmax, int classes)
        {
            using var w = new BinaryWriter(File.Create(Path.Combine(dir, name + ".trees")));
            w.Write("MCTR"u8.ToArray());
            w.Write(1); w.Write(softmax ? 1 : 0); w.Write(classes);
            int k = softmax ? classes : 1;
            w.Write(k);
            for (int i = 0; i < k; i++) w.Write(softmax ? 0.1 * i : -1.0);
            w.Write(0);
        }
        var models = new JsonObject();
        void Model(string name, bool softmax, int classes) { Trees(name, softmax, classes); models[name] = new JsonObject { ["trees_file"] = name + ".trees" }; }
        Model("none", false, 2);
        Model("time_zone", true, 5);
        Model("bpm_octave", true, 7);
        var moods = Enumerable.Range(Channels.FirstMood, Channels.MoodCount).Select(c => Channels.Names[c]).ToArray();
        foreach (var m in moods) Model("mood_" + m, false, 2);
        JsonObject PerMood(double v) { var o = new JsonObject(); foreach (var m in moods) o[m] = v; return o; }
        var json = new JsonObject
        {
            ["features"] = new JsonArray("dsp.bpm"), ["models"] = models, ["unclassifiable_threshold"] = 0.9,
            ["bpm_octave"] = new JsonObject { ["factors"] = new JsonArray(0.5, 1, 1, 1, 1, 1, 2), ["input_bpm"] = "dsp.bpm" },
        };
        if (weights) { json["mood_weights"] = PerMood(1); json["second_mood_threshold"] = 0.5; }
        if (thresholds) json["mood_thresholds"] = PerMood(0.5);
        File.WriteAllText(Path.Combine(dir, "model.json"), json.ToJsonString());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]                               // a newer model: mood_weights alone
    [InlineData(false, true)]                               // an older one: mood_thresholds alone
    public void Loads_with_either_mood_rule(bool weights, bool thresholds)
    {
        Write(_dir, weights, thresholds);
        var p = new ChannelModel(_dir).Predict(new Dictionary<string, double> { ["dsp.bpm"] = 120 });
        Assert.False(p.Unclassifiable);
        Assert.Equal(4, p.TimeZone);                        // the largest base margin, all zone weights 1
        Assert.NotEmpty(p.Moods);
        Assert.Equal(240, p.Bpm);                           // the last octave class has the largest margin: ×2
    }

    [Fact]
    public void Refuses_a_model_without_a_mood_rule()
    {
        Write(_dir, false, false);
        Assert.Throws<InvalidDataException>(() => new ChannelModel(_dir));
    }
}
