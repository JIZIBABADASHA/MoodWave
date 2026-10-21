using System.Runtime.InteropServices;
using Xunit;

namespace MoodChannels.Tests;

public class ClapWindowsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5.9, 0)]
    [InlineData(6, 1)]
    [InlineData(25, 2)]
    [InlineData(600, 12)]
    public void Count_one_per_full_ten_seconds_at_most_twelve(double seconds, int windows) =>
        Assert.Equal(windows, ClapWindows.Count((long)(seconds * ClapWindows.SampleRate)));

    [Fact]
    public void Settings_nest_with_all_twelve_windows()
    {
        // raising the setting only adds windows: the embeddings already worked out are kept
        var six = ClapWindows.Pick(12, 6);
        var nine = ClapWindows.Pick(12, 9);
        var twelve = ClapWindows.Pick(12, 12);
        Assert.Equal(6, six.Length);
        Assert.Equal(9, nine.Length);
        Assert.Subset(nine.ToHashSet(), six.ToHashSet());
        Assert.Subset(twelve.ToHashSet(), nine.ToHashSet());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(11)]
    public void Pick_stays_within_the_track(int windows)
    {
        foreach (int s in ClapWindows.SegmentChoices)
        {
            var pick = ClapWindows.Pick(windows, s);
            Assert.Equal(Math.Min(windows, s), pick.Length);
            Assert.All(pick, w => Assert.InRange(w, 0, windows - 1));
            Assert.Equal(pick.Distinct().Count(), pick.Length);
        }
    }

    [Fact]
    public void Mask_and_windows_round_trip() =>
        Assert.Equal(new[] { 0, 2, 11 }, ClapWindows.Windows(ClapWindows.Mask(new[] { 11, 0, 2 })));
}

public class ReadPcmTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(65536 * 3 + 2)]
    public void Reads_every_whole_sample(int bytes)
    {
        var samples = Enumerable.Range(0, bytes / 4 + 1).Select(i => (float)Math.Sin(i)).ToArray();
        var raw = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray()[..bytes];
        var pcm = Audio.ReadPcm(new TrickleStream(raw));
        Assert.Equal(samples[..(bytes / 4)], pcm);
    }

    /// <summary>A pipe that gives at most a few thousand bytes per read, as ffmpeg's stdout does.</summary>
    sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 4093));
    }
}

public class TreeModelTests
{
    /// <summary>A binary model of one tree: x[0] &lt; 0.5 → leaf −1, else +1 (NaN goes right).</summary>
    static string OneSplit()
    {
        string path = Path.GetTempFileName();
        using var w = new BinaryWriter(File.Create(path));
        w.Write("MCTR"u8.ToArray());
        w.Write(1); w.Write(0); w.Write(2);                 // version, binary logistic, classes
        w.Write(1); w.Write(0.25);                          // one base margin
        w.Write(1);                                         // trees
        w.Write(0); w.Write(3);                             // class 0, three nodes
        foreach (short f in new short[] { 0, -1, -1 }) w.Write(f);
        foreach (float v in new[] { 0.5f, -1f, 1f }) w.Write(v);
        foreach (int l in new[] { 1, -1, -1 }) w.Write(l);
        foreach (int r in new[] { 2, -1, -1 }) w.Write(r);
        foreach (byte d in new byte[] { 0, 0, 0 }) w.Write(d);
        return path;
    }

    static double Sigmoid(double z) => 1 / (1 + Math.Exp(-z));

    [Fact]
    public void Follows_the_split_and_adds_the_base()
    {
        var m = new TreeModel(OneSplit());
        Assert.Equal(Sigmoid(0.25 - 1), m.Predict(new[] { 0.2f })[1], 12);
        Assert.Equal(Sigmoid(0.25 + 1), m.Predict(new[] { 0.7f })[1], 12);
        Assert.Equal(Sigmoid(0.25 + 1), m.Predict(new[] { float.NaN })[1], 12);   // not defaultLeft: right
        Assert.Equal(1.0, m.Predict(new[] { 0.2f }).Sum(), 12);
    }

    [Fact]
    public void Rejects_trailing_data()
    {
        string path = OneSplit();
        File.AppendAllText(path, "x");
        Assert.Throws<InvalidDataException>(() => new TreeModel(path));
    }
}
