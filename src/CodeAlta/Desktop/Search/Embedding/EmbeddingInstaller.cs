using System.Text;
using CodeAlta.Desktop.Speech;
using CodeAlta.NativeEngines;

namespace CodeAlta.Desktop.Search.Embedding;

/// <summary>Where the download of a model of search by meaning is.</summary>
/// <param name="State"><c>idle</c>, <c>downloading</c>, <c>done</c>, <c>failed</c> or <c>canceled</c>.</param>
/// <param name="ModelId">The model.</param>
/// <param name="ReceivedBytes">The bytes on disk, of <paramref name="TotalBytes"/>.</param>
/// <param name="TotalBytes">The bytes of the whole download.</param>
/// <param name="Error">For <c>failed</c>: <c>network</c>, <c>not_found</c>, <c>checksum</c>, <c>busy_elsewhere</c>, <c>disk</c> or <c>unsupported_platform</c>.</param>
internal sealed record EmbeddingInstallSnapshot(string State, string? ModelId, long ReceivedBytes, long TotalBytes, string? Error)
{
    /// <summary>Gets the state before any download.</summary>
    public static EmbeddingInstallSnapshot Idle { get; } = new("idle", null, 0, 0, null);

    /// <summary>Gets whether a download runs.</summary>
    public bool IsRunning => State == "downloading";
}

/// <summary>
/// Downloads a model of search by meaning into the cache (<c>cache/search/models/&lt;id&gt;-&lt;revision&gt;</c>), and the
/// ONNX Runtime it runs on into the runtime folder speech shares (<c>cache/runtime/sherpa-onnx/&lt;version&gt;/&lt;rid&gt;</c>).
/// One download at a time; a file is resumed where it stopped and checked against its hash, and a folder gets its name
/// only once complete.
/// </summary>
internal sealed class EmbeddingInstaller
{
    private const string Marker = "model.json";

    private readonly string _modelsRoot;
    private readonly string _runtimeRoot;
    private readonly string _speechRoot;
    private readonly SpeechEngineDefinition? _engine;
    private readonly PinnedDownloader _downloads;
    private readonly Lock _gate = new();
    private EmbeddingInstallSnapshot _snapshot = EmbeddingInstallSnapshot.Idle;
    private CancellationTokenSource? _cancellation;
    private Task _active = Task.CompletedTask;
    private bool _closed;

    /// <summary>Creates the installer.</summary>
    /// <param name="searchCacheRoot">The folder of search in the cache.</param>
    /// <param name="runtimeRoot">The folder of the native runtimes in the cache.</param>
    /// <param name="speechRoot">The folder of speech in the cache, where an older runtime may still be.</param>
    /// <param name="engine">The ONNX Runtime of this platform, or null when it has none.</param>
    /// <param name="handler">The handler of the requests, or null for the network.</param>
    public EmbeddingInstaller(string searchCacheRoot, string runtimeRoot, string speechRoot, SpeechEngineDefinition? engine, HttpMessageHandler? handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(searchCacheRoot);
        ArgumentException.ThrowIfNullOrEmpty(runtimeRoot);
        ArgumentException.ThrowIfNullOrEmpty(speechRoot);
        _modelsRoot = Path.Combine(Path.GetFullPath(searchCacheRoot), "models");
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
        _speechRoot = Path.GetFullPath(speechRoot);
        _engine = engine;
        _downloads = new(handler, new("CodeAlta", DesktopCommandLine.Version));
    }

    /// <summary>Raised when <see cref="Current"/> changed.</summary>
    public event Action<EmbeddingInstallSnapshot>? Changed;

    /// <summary>Gets where the download is.</summary>
    public EmbeddingInstallSnapshot Current { get { lock (_gate) return _snapshot; } }

    /// <summary>Gets whether this platform has the ONNX Runtime the models run on.</summary>
    public bool Supported => _engine is not null;

    /// <summary>Gets the folder of a model.</summary>
    public string DirectoryOf(EmbeddingModelDefinition model) => Path.Combine(_modelsRoot, model.FolderName);

    /// <summary>Gets whether a model is downloaded whole.</summary>
    public bool IsInstalled(EmbeddingModelDefinition model)
    {
        var directory = DirectoryOf(model);
        return File.Exists(Path.Combine(directory, Marker)) && model.Files.All(file => new FileInfo(Path.Combine(directory, file.Name)) is { Exists: true } info && info.Length == file.Size);
    }

    /// <summary>Gets the bytes a model takes on disk; zero when it is not there.</summary>
    public long BytesOf(EmbeddingModelDefinition model)
        => IsInstalled(model) ? model.Files.Sum(file => new FileInfo(Path.Combine(DirectoryOf(model), file.Name)).Length) : 0;

    /// <summary>Gets the full path of the ONNX Runtime library when it is installed, or null.</summary>
    public string? RuntimeLibrary()
    {
        if (_engine is null) return null;
        var directory = SherpaRuntime.Resolve(_runtimeRoot, _speechRoot, _engine);
        return SherpaRuntime.IsInstalled(directory, _engine) ? SherpaRuntime.OnnxRuntimePathOf(directory, _engine) : null;
    }

    /// <summary>Starts the download of a model and of the runtime, unless one runs; does nothing when both are there.</summary>
    /// <returns>The download, which ends in <c>done</c>, <c>failed</c> or <c>canceled</c>.</returns>
    public Task InstallAsync(EmbeddingModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            if (_closed) return Task.CompletedTask;
            if (_snapshot.IsRunning) return _active;
            if (_engine is null)
            {
                Publish(new("failed", model.Id, 0, 0, "unsupported_platform"));
                return Task.CompletedTask;
            }

            _cancellation?.Dispose();
            _cancellation = new();
            var token = _cancellation.Token;
            var total = model.Size + (RuntimeLibrary() is null ? _engine.Size : 0);
            Publish(new("downloading", model.Id, 0, total, null));
            _active = Task.Run(() => RunAsync(model, total, token));
            return _active;
        }
    }

    private async Task RunAsync(EmbeddingModelDefinition model, long total, CancellationToken cancellationToken)
    {
        var progress = new Progress(this, model.Id, total);
        try
        {
            if (RuntimeLibrary() is null) await SherpaRuntime.InstallAsync(_downloads, _runtimeRoot, _engine!, progress, null, cancellationToken).ConfigureAwait(false);
            if (!IsInstalled(model))
            {
                Directory.CreateDirectory(_modelsRoot);
                var marker = Encoding.UTF8.GetBytes($"{{\"id\":\"{model.Id}\",\"revision\":\"{model.Revision}\"}}");
                await _downloads.InstallFolderAsync(DirectoryOf(model), Marker,
                    [.. model.Files.Select(file => (new PinnedFile(file.Name, file.Size, file.Sha256), file.Address))], null, marker, progress, cancellationToken).ConfigureAwait(false);
            }

            Publish(new("done", model.Id, total, total, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(new("canceled", model.Id, progress.Bytes, total, null));
        }
        catch (PinnedDownloadException exception)
        {
            Publish(new("failed", model.Id, progress.Bytes, total, exception.Code));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Publish(new("failed", model.Id, progress.Bytes, total, "disk"));
        }
    }

    /// <summary>Stops the download that runs; what is on disk is resumed by the next one.</summary>
    public void Cancel()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    /// <summary>Removes a model from the disk.</summary>
    /// <returns>True when nothing of it is left.</returns>
    public bool Delete(EmbeddingModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            if (_snapshot.IsRunning && _snapshot.ModelId == model.Id) return false;
        }

        try
        {
            var directory = DirectoryOf(model);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A worker that still holds the model: it is removed at the next start.
            return false;
        }
    }

    /// <summary>Stops the download and waits for it.</summary>
    public async Task CloseAsync()
    {
        Task active;
        lock (_gate)
        {
            _closed = true;
            _cancellation?.Cancel();
            active = _active;
        }

        try { await active.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private void Publish(EmbeddingInstallSnapshot snapshot)
    {
        lock (_gate) _snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }

    private sealed class Progress(EmbeddingInstaller owner, string model, long total) : IPinnedDownloadProgress
    {
        private long _received;
        private long _published;

        public long Bytes => Interlocked.Read(ref _received);

        public void Received(long bytes)
        {
            var received = Interlocked.Add(ref _received, bytes);
            // A few updates per percent are enough for the page.
            if (received - Interlocked.Read(ref _published) < Math.Max(256 * 1024, total / 400) && received < total) return;
            Interlocked.Exchange(ref _published, received);
            owner.Publish(new("downloading", model, Math.Min(received, total), total, null));
        }

        public void Checking(PinnedFile file)
        {
        }
    }
}
