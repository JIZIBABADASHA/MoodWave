using System.Security.Cryptography;
using System.Text.Json;

namespace MoodChannels.Service;

/// <summary>
/// The advanced AI's model files (models/manifest.json, "clap"), downloaded into the data folder
/// (models\clap\ there) when the listener turns the advanced AI on, each checked against its SHA-256 before it is
/// used. A download goes to a .part file first and goes on from there after an interruption (HTTP range requests).
///
/// While the manifest has no <c>url</c> (no release yet, during development) the files are copied instead, from
/// models\ of a local checkout: MOODWAVE_MODEL_SOURCE, else the module's own models\ (a checkout), else the folder
/// named in models\source.txt (written by MikuDeploy). The same checks apply.
/// </summary>
sealed class ClapModel
{
    const string Key = "clap";
    const int Buffer = 1 << 20;
    /// <summary>A download that gets nothing for this long has stalled: it stops with an error (Download goes on from there).</summary>
    static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    sealed record FileSpec(string Path, long Bytes, string Sha256);

    readonly string _moduleDir, _dir;
    readonly Action<string> _log;
    readonly object _lock = new();
    readonly string? _version, _url, _manifestError;
    readonly List<FileSpec> _files = new();
    CancellationTokenSource? _cts;
    Task? _download;
    long _done;
    string? _error;
    bool _ready;

    /// <summary>The download finished (ready) or stopped (cancelled, failed).</summary>
    public event Action? Changed;

    public ClapModel(string moduleDir, string dataDir, Action<string> log)
    {
        (_moduleDir, _dir, _log) = (moduleDir, Path.Combine(dataDir, "models"), log);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(moduleDir, "models", "manifest.json")));
            var m = doc.RootElement.GetProperty("models").GetProperty(Key);
            _version = m.GetProperty("version").GetString();
            _url = m.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            foreach (var f in m.GetProperty("files").EnumerateArray())
                _files.Add(new FileSpec(f.GetProperty("path").GetString()!, f.GetProperty("bytes").GetInt64(), f.GetProperty("sha256").GetString()!));
        }
        catch (Exception ex) { _manifestError = "models/manifest.json: " + ex.Message; }
        _ready = Verified();
    }

    /// <summary>The ONNX file once every file is there and checked; null otherwise.</summary>
    public string? OnnxPath => _ready ? Path.Combine(_dir, _files.First(f => f.Path.EndsWith(".onnx")).Path) : null;

    public bool Ready => _ready;

    public bool Downloading { get { lock (_lock) return _download is { IsCompleted: false }; } }

    long Total => _files.Sum(f => f.Bytes);

    string Marker => Path.Combine(_dir, Key, ".verified");

    /// <summary>
    /// Whether the files in the data folder are this version's: checked against the SHA-256 once, after the download,
    /// then known by the marker (the version, each file's size and time), so a start does not read 300 MB.
    /// </summary>
    bool Verified()
    {
        if (_manifestError != null || _files.Count == 0) return false;
        try { return File.Exists(Marker) && File.ReadAllText(Marker) == Stamp(); } catch (Exception) { return false; }
    }

    string Stamp() => _version + "\n" + string.Join("\n", _files.Select(f =>
    {
        var fi = new FileInfo(Path.Combine(_dir, f.Path));
        return fi.Exists ? $"{f.Path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : f.Path + "|missing";
    }));

    /// <summary>Starts the download (nothing when ready or already downloading).</summary>
    public void Download()
    {
        lock (_lock)
        {
            if (_ready || _manifestError != null || _download is { IsCompleted: false }) return;
            _error = null;
            _done = 0;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _download = Task.Run(() => Run(token));
        }
        Changed?.Invoke();
    }

    public void Cancel()
    {
        lock (_lock) _cts?.Cancel();
    }

    async Task Run(CancellationToken token)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            long before = 0;
            foreach (var f in _files)
            {
                string dst = Path.Combine(_dir, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!(File.Exists(dst) && new FileInfo(dst).Length == f.Bytes && Hash(dst) == f.Sha256))
                {
                    string part = dst + ".part";
                    await Fetch(f, part, before, token);
                    string sha = Hash(part);
                    if (sha != f.Sha256)
                    {
                        File.Delete(part);
                        throw new InvalidDataException($"{f.Path}: the file is not the one listed (SHA-256 {sha[..12]}…, want {f.Sha256[..12]}…)");
                    }
                    File.Move(part, dst, overwrite: true);
                }
                before += f.Bytes;
                Interlocked.Exchange(ref _done, before);
            }
            File.WriteAllText(Marker, Stamp());
            lock (_lock) _ready = true;
            _log($"CLAP model {_version} ready ({Total / 1e6:0} MB in {sw.Elapsed.TotalSeconds:0} s)");
        }
        catch (OperationCanceledException) { _log("CLAP model download cancelled"); }
        catch (Exception ex)
        {
            lock (_lock) _error = ex.Message;
            _log("CLAP model download failed: " + ex.Message);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// One file into <paramref name="part"/>: copied from a local copy when there is one (a checkout, a test; checked the
    /// same), else from the release, going on from what is there.
    /// </summary>
    async Task Fetch(FileSpec f, string part, long before, CancellationToken token)
    {
        string? local = LocalSource(f);
        if (local == null && _url == null)
            throw new FileNotFoundException(
                $"no download address (models/manifest.json) and no local copy of {f.Path} (MOODWAVE_MODEL_SOURCE, models\\ of a checkout)");
        if (local != null)
        {
            string src = local;
            await using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, FileOptions.SequentialScan | FileOptions.Asynchronous);
            await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, Buffer, FileOptions.Asynchronous);
            await Copy(input, output, before, token);
            return;
        }
        // no overall time limit (300 MB on a slow line takes long), but one for each wait: the answer, then each read
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MoodWave");
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have >= f.Bytes) { File.Delete(part); have = 0; }
            var req = new HttpRequestMessage(HttpMethod.Get, _url!.TrimEnd('/') + "/" + Path.GetFileName(f.Path));
            if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            stalled.CancelAfter(Stall);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stalled.Token);
            resp.EnsureSuccessStatusCode();
            bool resumed = have > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
            await using var body = await resp.Content.ReadAsStreamAsync(stalled.Token);
            await using var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, Buffer, FileOptions.Asynchronous);
            await Copy(body, file, before + (resumed ? have : 0), stalled.Token, stalled);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"the download stalled (nothing for {Stall.TotalSeconds:0} s); start it again to go on");
        }
    }

    /// <summary>Copies <paramref name="input"/>; with <paramref name="stalled"/> (its token), each read must come within <see cref="Stall"/>.</summary>
    async Task Copy(Stream input, Stream output, long start, CancellationToken token, CancellationTokenSource? stalled = null)
    {
        var buf = new byte[Buffer];
        long done = start;
        int n;
        while (true)
        {
            stalled?.CancelAfter(Stall);
            if ((n = await input.ReadAsync(buf, token)) <= 0) break;
            await output.WriteAsync(buf.AsMemory(0, n), token);
            done += n;
            Interlocked.Exchange(ref _done, done);
        }
    }

    string? LocalSource(FileSpec f)
    {
        var dirs = new List<string?> { Environment.GetEnvironmentVariable("MOODWAVE_MODEL_SOURCE"), Path.Combine(_moduleDir, "models") };
        try { dirs.Add(File.ReadAllText(Path.Combine(_moduleDir, "models", "source.txt")).Trim()); } catch (Exception) { }
        return dirs.Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d!, f.Path)).FirstOrDefault(File.Exists);
    }

    static string Hash(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>
    /// Deletes the downloaded files (the listener removed the AI data); a download running is stopped first. Returns
    /// false when they could not all be deleted (in use, no permission: the reason is in the status's error).
    /// </summary>
    public bool Delete()
    {
        Task? running;
        lock (_lock) { _cts?.Cancel(); running = _download; }
        try { running?.Wait(TimeSpan.FromSeconds(10)); } catch (Exception) { }
        lock (_lock)
        {
            _ready = false;
            _error = null;
            _done = 0;
        }
        bool deleted = true;
        try { Directory.Delete(Path.Combine(_dir, Key), recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            deleted = false;
            lock (_lock) _error = "the model could not be deleted: " + ex.Message;
            _log("CLAP model not deleted: " + ex.Message);
        }
        Changed?.Invoke();
        return deleted;
    }

    public object Status()
    {
        lock (_lock)
            return new
            {
                version = _version, ready = _ready, downloading = _download is { IsCompleted: false },
                bytes = Total, done = _ready ? Total : Interlocked.Read(ref _done), error = _error ?? _manifestError,
                local = _url == null || _files.Count > 0 && LocalSource(_files[0]) != null,
            };
    }
}
