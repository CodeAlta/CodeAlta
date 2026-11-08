using System.Globalization;
using System.Text.Json;
using CodeAlta.Core.IO;
using CodeAlta.ModelServers.HuggingFace;
using CodeAlta.ModelServers.Store;

namespace CodeAlta.ModelServers.Downloads;

/// <summary>
/// The downloads of models of a machine: a queue that runs a few at once, kept in a file so that a restart resumes them,
/// a check of the disk before each one, the SHA-256 of every file checked, and the files put in the store only once checked.
/// It downloads only the GGUF files of the quantization asked for and its vision projector: never a <c>preset.ini</c> of the
/// repository, which llama.cpp would apply with any option.
/// </summary>
public sealed class ModelDownloads : IAsyncDisposable
{
    /// <summary>What a download keeps free on the disk beyond its own size.</summary>
    public const long DiskMargin = 2L * 1024 * 1024 * 1024;

    private static readonly TimeSpan KeepEnded = TimeSpan.FromDays(1);
    private readonly ModelStore _store;
    private readonly HuggingFaceClient _hub;
    private readonly RangeDownloader _downloader;
    private readonly Func<int> _atOnce;
    private readonly string _statePath;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<DownloadInfo> _jobs = [];
    private readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private long _lastNotify;

    /// <summary>Creates the downloads of a store.</summary>
    /// <param name="store">The store the models go to.</param>
    /// <param name="hub">The Hub.</param>
    /// <param name="downloader">The downloader of the files.</param>
    /// <param name="atOnce">How many downloads run at once.</param>
    /// <param name="statePath">The file the jobs are kept in.</param>
    /// <param name="time">The clock.</param>
    public ModelDownloads(ModelStore store, HuggingFaceClient hub, RangeDownloader downloader, Func<int> atOnce, string statePath, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(atOnce);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _hub = hub;
        _downloader = downloader;
        _atOnce = atOnce;
        _statePath = statePath;
        _time = time;
        Load();
    }

    /// <summary>Raised when a download changed: at most twice a second while bytes come.</summary>
    public event Action? Changed;

    /// <summary>Raised once when a download ends: done, failed or cancelled.</summary>
    public event Action<DownloadInfo>? Ended;

    /// <summary>Lists the downloads, the running ones first.</summary>
    /// <returns>The downloads.</returns>
    public IReadOnlyList<DownloadInfo> List()
    {
        lock (_gate)
        {
            return _jobs.OrderBy(static job => job.State is "downloading" or "checking" ? 0 : job.State is "queued" or "paused" ? 1 : 2)
                .ThenByDescending(static job => job.CreatedAt).ToList();
        }
    }

    /// <summary>Starts the queued downloads (those a restart found too).</summary>
    public void Start() => Pump();

    /// <summary>Asks for a model.</summary>
    /// <param name="model">The model, <c>org/repo:TAG</c>.</param>
    /// <param name="requestedBy">Who asks, or null.</param>
    /// <returns>Its download: the one already running for that model, or a new one.</returns>
    /// <exception cref="ModelHostException">The model is not one.</exception>
    public DownloadInfo Request(string model, string? requestedBy)
    {
        var (repo, tag) = ModelStore.ParseId(model);
        var id = $"{repo}:{tag}";
        DownloadInfo job;
        lock (_gate)
        {
            if (_jobs.FirstOrDefault(existing => existing.Model == id && existing.State is "queued" or "downloading" or "checking" or "paused") is { } existing)
            {
                if (existing.State == "paused") Replace(existing with { State = "queued" });
                job = Current(existing.Id)!;
            }
            else
            {
                job = new DownloadInfo("dl-" + Guid.NewGuid().ToString("N")[..8], id, "queued", 0, 0, 0, null, null, 0, _time.GetUtcNow(), null, null, requestedBy);
                _jobs.Add(job);
            }

            Save();
        }

        Notify(force: true);
        Pump();
        return job;
    }

    /// <summary>Pauses a download; what is on disk stays.</summary>
    /// <param name="id">The download.</param>
    /// <returns>It, or null when not found.</returns>
    public DownloadInfo? Pause(string id) => Stop(id, "paused");

    /// <summary>Resumes a paused or failed download.</summary>
    /// <param name="id">The download.</param>
    /// <returns>It, or null when not found.</returns>
    public DownloadInfo? Resume(string id)
    {
        lock (_gate)
        {
            if (Current(id) is not { State: "paused" or "failed" } job) return Current(id);
            Replace(job with { State = "queued", Error = null, ErrorCode = null, EndedAt = null });
            Save();
        }

        Notify(force: true);
        Pump();
        return Current(id);
    }

    /// <summary>Cancels a download and removes what it wrote.</summary>
    /// <param name="id">The download.</param>
    /// <returns>It, or null when not found.</returns>
    public DownloadInfo? Cancel(string id)
    {
        var job = Stop(id, "cancelled");
        if (job is not null && !IsRunning(id)) _ = Task.Run(() => DeletePartsAsync(job));
        return job;
    }

    /// <summary>Removes the downloads that ended from the list.</summary>
    public void ClearEnded()
    {
        lock (_gate)
        {
            _jobs.RemoveAll(static job => job.State is "done" or "failed" or "cancelled");
            Save();
        }

        Notify(force: true);
    }

    private DownloadInfo? Stop(string id, string state)
    {
        Running? running;
        lock (_gate)
        {
            if (Current(id) is not { } job || job.State is "done" or "cancelled") return Current(id);
            _running.TryGetValue(id, out running);
            Replace(job with { State = state, BytesPerSecond = 0, EndedAt = state == "cancelled" ? _time.GetUtcNow() : null });
            Save();
        }

        running?.Stop.Cancel();
        Notify(force: true);
        if (state == "cancelled" && Current(id) is { } ended) Ended?.Invoke(ended);
        return Current(id);
    }

    private bool IsRunning(string id)
    {
        lock (_gate)
        {
            return _running.ContainsKey(id);
        }
    }

    private void Pump()
    {
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested) return;
            foreach (var job in _jobs.Where(static job => job.State == "queued").OrderBy(static job => job.CreatedAt).ToList())
            {
                if (_running.Count >= Math.Max(1, _atOnce())) break;
                var running = new Running(CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
                _running[job.Id] = running;
                Replace(job with { State = "downloading" });
                running.Task = Task.Run(() => RunAsync(job.Id, running));
            }
        }
    }

    private async Task RunAsync(string id, Running running)
    {
        var token = running.Stop.Token;
        try
        {
            var job = Current(id)!;
            var (repo, tag) = ModelStore.ParseId(job.Model);
            var details = await _hub.GetModelAsync(repo, token).ConfigureAwait(false);
            var revision = job.Revision ?? details.Revision;
            var files = job.Revision is null || job.Revision == details.Revision ? details.Files : await _hub.ListFilesAsync(repo, revision, token).ConfigureAwait(false);
            var quant = GgufNames.Find(files, tag) ?? throw new DownloadException("not_found", $"{repo} has no {tag} quantization.");
            var have = _store.Find(job.Model) is { Revision: var r } existing && r == revision ? existing.Files.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal) : [];
            var missing = quant.Files.Where(file => !have.Contains(file.Path)).ToList();
            var total = quant.Files.Sum(static file => Math.Max(0, file.Size));
            var already = quant.Files.Where(file => have.Contains(file.Path)).Sum(static file => Math.Max(0, file.Size));
            Update(id, current => current with { Revision = revision, Total = total, Done = already, Files = quant.Files.Count });
            var onDisk = missing.Sum(file => file.Sha256 is { } sha && File.Exists(_store.PartPath(repo, sha)) ? new FileInfo(_store.PartPath(repo, sha)).Length : 0);
            var need = missing.Sum(static file => Math.Max(0, file.Size)) - onDisk;
            var free = _store.FreeBytes();
            if (free > 0 && need + DiskMargin > free)
                throw new DownloadException("disk", string.Create(CultureInfo.InvariantCulture,
                    $"The disk needs {Gb(need + DiskMargin)} free for {job.Model} and has {Gb(free)}: free some space, or keep models on another disk."));

            foreach (var file in missing)
            {
                var sha = file.Sha256 ?? throw new DownloadException("checksum", $"{file.Path} has no SHA-256 in the repository: it is not downloaded.");
                var part = _store.PartPath(repo, sha);
                await _downloader.DownloadAsync(cancellationToken => _hub.ResolveAsync(repo, revision, file.Path, cancellationToken), part, file.Size, sha, 4,
                    bytes => Received(id, running, bytes), () => Update(id, static current => current with { State = "checking" }), token).ConfigureAwait(false);
                _store.Place(repo, revision, file.Path, part, sha);
                Update(id, static current => current with { State = "downloading" });
            }

            End(id, running, current => current with { State = "done", Done = current.Total, BytesPerSecond = 0, EndedAt = _time.GetUtcNow() });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            End(id, running, null);
            if (Current(id) is { State: "cancelled" } cancelled) await DeletePartsAsync(cancelled).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DownloadException or ModelHostException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            var code = exception switch { DownloadException d => d.Code, ModelHostException m => m.Code, IOException => "disk", UnauthorizedAccessException => "disk", _ => "network" };
            End(id, running, current => current with { State = "failed", Error = exception.Message, ErrorCode = code, BytesPerSecond = 0, EndedAt = _time.GetUtcNow() });
        }
    }

    private void Received(string id, Running running, long bytes)
    {
        var now = _time.GetTimestamp();
        double speed;
        lock (_gate)
        {
            running.Samples.Enqueue((now, bytes));
            while (running.Samples.Count > 0 && _time.GetElapsedTime(running.Samples.Peek().At, now) > TimeSpan.FromSeconds(5)) running.Samples.Dequeue();
            var window = running.Samples.Count > 1 ? Math.Max(0.5, _time.GetElapsedTime(running.Samples.Peek().At, now).TotalSeconds) : 1;
            speed = Math.Max(0, running.Samples.Sum(static sample => sample.Bytes) / window);
            if (Current(id) is { } job) Replace(job with { Done = Math.Max(0, job.Done + bytes), BytesPerSecond = speed });
        }

        Notify(force: false);
    }

    private void End(string id, Running running, Func<DownloadInfo, DownloadInfo>? change)
    {
        DownloadInfo? ended = null;
        lock (_gate)
        {
            _running.Remove(id);
            if (change is not null && Current(id) is { } job)
            {
                ended = change(job);
                Replace(ended);
            }

            Save();
        }

        running.Stop.Dispose();
        Notify(force: true);
        if (ended is not null) Ended?.Invoke(ended);
        Pump();
    }

    private async Task DeletePartsAsync(DownloadInfo job)
    {
        try
        {
            var (repo, tag) = ModelStore.ParseId(job.Model);
            if (job.Revision is null) return;
            var files = await _hub.ListFilesAsync(repo, job.Revision, CancellationToken.None).ConfigureAwait(false);
            foreach (var file in GgufNames.Find(files, tag)?.Files ?? [])
            {
                if (file.Sha256 is not { } sha) continue;
                var part = _store.PartPath(repo, sha);
                File.Delete(part);
                File.Delete(part + ".chunks");
            }
        }
        catch (Exception exception) when (exception is ModelHostException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            // What stays is found again by a new download of the same model, which resumes it.
        }
    }

    private void Update(string id, Func<DownloadInfo, DownloadInfo> change)
    {
        lock (_gate)
        {
            if (Current(id) is { } job) Replace(change(job));
            Save();
        }

        Notify(force: true);
    }

    private DownloadInfo? Current(string id)
    {
        lock (_gate)
        {
            return _jobs.FirstOrDefault(job => job.Id == id);
        }
    }

    private void Replace(DownloadInfo job)
    {
        var index = _jobs.FindIndex(existing => existing.Id == job.Id);
        if (index >= 0) _jobs[index] = job;
    }

    private void Notify(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastNotify) < 500) return;
        Interlocked.Exchange(ref _lastNotify, now);
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var jobs = JsonSerializer.Deserialize(File.ReadAllText(_statePath), ModelServersJsonContext.Default.ListDownloadInfo) ?? [];
            var now = _time.GetUtcNow();
            foreach (var job in jobs)
            {
                if (job.EndedAt is { } ended && now - ended > KeepEnded) continue;
                // A download that ran when the host stopped resumes.
                _jobs.Add(job.State is "downloading" or "checking" ? job with { State = "queued", BytesPerSecond = 0 } : job with { BytesPerSecond = 0 });
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            SafeFile.WriteAllText(_statePath, JsonSerializer.Serialize(_jobs.Select(static job => job with { BytesPerSecond = 0 }).ToList(), ModelServersJsonContext.Default.ListDownloadInfo));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Gb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e9:0.0} GB");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            _stopping.Cancel();
            tasks = _running.Values.Select(static running => running.Task).OfType<Task>().ToArray();
        }

        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { }

        lock (_gate)
        {
            // A download that the host stopped resumes at its next start.
            foreach (var job in _jobs.Where(static job => job.State is "downloading" or "checking").ToList()) Replace(job with { State = "queued" });
            Save();
        }

        _stopping.Dispose();
    }

    private sealed class Running(CancellationTokenSource stop)
    {
        public CancellationTokenSource Stop { get; } = stop;

        public Task? Task { get; set; }

        public Queue<(long At, long Bytes)> Samples { get; } = new();
    }
}
