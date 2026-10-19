using System.Text;

namespace MoodChannels;

/// <summary>
/// A gradient-boosted tree ensemble (XGBoost, binary logistic or softmax) read from a ".trees" file, evaluated here:
/// loading takes a fraction of a second, one prediction well under a millisecond for all the channel models.
/// <para>File (little-endian): "MCTR", int32 version 1, int32 objective (0 = binary logistic, 1 = softmax),
/// int32 classes, int32 base count + float64 base margins, int32 trees; per tree int32 class, int32 nodes, then
/// int16 feature[] (-1 = leaf), float32 value[] (threshold, or the leaf's value), int32 left[], int32 right[],
/// uint8 defaultLeft[].</para>
/// Rule: x[feature] &lt; value goes left, otherwise right; NaN goes left when defaultLeft. The margin of a class is
/// its base plus the leaves its trees end in; binary: p = sigmoid(margin), probabilities [1 - p, p]; otherwise softmax.
/// Immutable after loading: thread-safe.
/// </summary>
public sealed class TreeModel
{
    readonly bool _softmax;
    readonly double[] _base;
    readonly int[] _treeClass, _treeRoot;     // per tree: its class and the index of its root in the node arrays
    readonly short[] _feature;
    readonly float[] _value;
    readonly int[] _left, _right;             // absolute node indices
    readonly bool[] _defaultLeft;

    /// <summary>Number of probabilities <see cref="Predict"/> returns.</summary>
    public int Classes { get; }

    public TreeModel(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
        if (new string(r.ReadChars(4)) != "MCTR") throw new InvalidDataException(path + ": not a tree model");
        if (r.ReadInt32() != 1) throw new InvalidDataException(path + ": unknown version");
        _softmax = r.ReadInt32() == 1;
        Classes = r.ReadInt32();
        _base = new double[r.ReadInt32()];
        for (int i = 0; i < _base.Length; i++) _base[i] = r.ReadDouble();
        int trees = r.ReadInt32();
        _treeClass = new int[trees];
        _treeRoot = new int[trees];
        var feature = new List<short>();
        var value = new List<float>();
        var left = new List<int>();
        var right = new List<int>();
        var dl = new List<bool>();
        for (int t = 0; t < trees; t++)
        {
            _treeClass[t] = r.ReadInt32();
            int n = r.ReadInt32(), at = feature.Count;
            _treeRoot[t] = at;
            for (int i = 0; i < n; i++) feature.Add(r.ReadInt16());
            for (int i = 0; i < n; i++) value.Add(r.ReadSingle());
            for (int i = 0; i < n; i++) left.Add(at + r.ReadInt32());
            for (int i = 0; i < n; i++) right.Add(at + r.ReadInt32());
            for (int i = 0; i < n; i++) dl.Add(r.ReadByte() != 0);
        }
        if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException(path + ": trailing data");
        (_feature, _value, _left, _right, _defaultLeft) = (feature.ToArray(), value.ToArray(), left.ToArray(), right.ToArray(), dl.ToArray());
    }

    /// <summary>Class probabilities for one feature vector (NaN = missing).</summary>
    public double[] Predict(ReadOnlySpan<float> x)
    {
        int k = _softmax ? Classes : 1;
        var margin = new double[k];
        for (int c = 0; c < k; c++) margin[c] = _base[c];
        for (int t = 0; t < _treeRoot.Length; t++)
        {
            int n = _treeRoot[t];
            while (_feature[n] >= 0)
            {
                float v = x[_feature[n]];
                n = float.IsNaN(v) ? (_defaultLeft[n] ? _left[n] : _right[n]) : v < _value[n] ? _left[n] : _right[n];
            }
            margin[_treeClass[t]] += _value[n];
        }
        if (!_softmax)
        {
            double p = 1 / (1 + Math.Exp(-margin[0]));
            return new[] { 1 - p, p };
        }
        double max = margin.Max(), sum = 0;
        for (int c = 0; c < k; c++) sum += margin[c] = Math.Exp(margin[c] - max);
        for (int c = 0; c < k; c++) margin[c] /= sum;
        return margin;
    }
}
