using System.IO.Compression;
using CodeAlta.NativeEngines;

namespace CodeAlta.Desktop.Speech;

/// <summary>
/// The native libraries of sherpa-onnx (its C API and the ONNX Runtime it ships) in the shared cache, one folder
/// per version and platform: <c>cache/runtime/sherpa-onnx/&lt;version&gt;/&lt;rid&gt;</c>. Speech loads both libraries;
/// search by meaning and the Pseudonymizer load the ONNX Runtime alone, so all map the same file. The application has
/// one instance, which all of them ask for the libraries: one download runs at a time, and who asks while it runs
/// waits for the same one.
/// </summary>
internal sealed class SherpaRuntime
{
    /// <summary>The file whose presence says the folder is complete.</summary>
    public const string Marker = "engine.json";

    private readonly string _runtimeRoot;
    private readonly string _speechRoot;
    private readonly PinnedDownloader _downloads;
    private readonly CancellationTokenSource _closing = new();
    private readonly Lock _gate = new();
    private Download? _download;

    /// <summary>Creates the runtime of a cache.</summary>
    /// <param name="runtimeRoot">The folder of the native runtimes in the cache (<c>CatalogOptions.RuntimeRoot</c>).</param>
    /// <param name="speechRoot">The folder of speech in the cache, where an older engine may still be.</param>
    /// <param name="engine">The engine of this platform, or null when it has none.</param>
    /// <param name="handler">The handler of the requests, or null for the network.</param>
    public SherpaRuntime(string runtimeRoot, string speechRoot, SpeechEngineDefinition? engine, HttpMessageHandler? handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(runtimeRoot);
        ArgumentException.ThrowIfNullOrEmpty(speechRoot);
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
        _speechRoot = Path.GetFullPath(speechRoot);
        Engine = engine;
        _downloads = new(handler, new("CodeAlta", DesktopCommandLine.Version));
    }

    /// <summary>Gets the engine of this platform, or null when it has none.</summary>
    public SpeechEngineDefinition? Engine { get; }

    /// <summary>
    /// Gets the folder that holds the engine now: the shared one, after moving there what speech downloaded before; the
    /// old folder when the move failed (a worker still holds its files); the shared one, where a download puts it, when
    /// neither holds it.
    /// </summary>
    public string InstallDirectory
        => FindInstalled() ?? (Engine is null ? Path.Combine(_runtimeRoot, "sherpa-onnx", "none") : DirectoryOf(_runtimeRoot, Engine));

    /// <summary>Gets whether the engine is on disk.</summary>
    public bool IsInstalled => FindInstalled() is not null;

    /// <summary>Gets the folder of an engine under the runtime root.</summary>
    public static string DirectoryOf(string runtimeRoot, SpeechEngineDefinition engine)
        => Path.Combine(runtimeRoot, "sherpa-onnx", SpeechModelCatalog.EngineVersion, engine.RuntimeIdentifier);

    /// <summary>Gets the folder speech used before the runtime was shared: <c>speech/runtime/&lt;version&gt;/&lt;rid&gt;</c>.</summary>
    public static string LegacyDirectoryOf(string speechRoot, SpeechEngineDefinition engine)
        => Path.Combine(speechRoot, "runtime", SpeechModelCatalog.EngineVersion, engine.RuntimeIdentifier);

    /// <summary>Gets the full path of the ONNX Runtime library when the engine is installed: the first library, loaded before the C API.</summary>
    /// <returns>The path, or null.</returns>
    public string? OnnxRuntimeLibrary() => FindInstalled() is { } directory ? Path.Combine(directory, Engine!.Libraries[0]) : null;

    /// <summary>
    /// Downloads the package of the engine and unpacks its libraries into the shared folder; does nothing when the
    /// engine is there. A call made while a download runs waits for that download: stopping the wait does not stop the
    /// download, which the others wait for.
    /// </summary>
    /// <param name="progress">Where the bytes on disk are told, those of an engine that is there included; or null.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <exception cref="PinnedDownloadException">The download failed.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform has no engine.</exception>
    public async Task InstallAsync(IPinnedDownloadProgress? progress, CancellationToken cancellationToken)
    {
        if (Engine is null) throw new PlatformNotSupportedException("This platform has no ONNX Runtime.");
        Download? download;
        // Only the decision is made under the gate: what a caller does with its progress is its own business.
        lock (_gate)
        {
            if (_download is null && !IsInstalled)
            {
                _closing.Token.ThrowIfCancellationRequested();
                _download = new Download(DownloadAsync);
            }

            download = _download;
        }

        if (download is null)
        {
            progress?.Received(Engine.Size);
            return;
        }

        if (progress is not null) download.Add(progress);
        try
        {
            await download.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (progress is not null) download.Remove(progress);
        }
    }

    /// <summary>Stops the download that runs, and waits for it to end.</summary>
    public async Task CloseAsync()
    {
        Task running;
        lock (_gate)
        {
            _closing.Cancel();
            running = _download?.Completion ?? Task.CompletedTask;
        }

        await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _downloads.Dispose();
    }

    private async Task DownloadAsync(Download download)
    {
        try
        {
            var engine = Engine!;
            var package = new PinnedFile("engine.nupkg", engine.Size, engine.Sha256);
            await _downloads.InstallFolderAsync(DirectoryOf(_runtimeRoot, engine), Marker, [(package, engine.Address)], (directory, _) =>
            {
                Extract(directory, package, engine);
                return Task.CompletedTask;
            }, "{}"u8.ToArray(), download, _closing.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _download = null;
        }
    }

    // The folder that holds the engine now, or null when none does.
    private string? FindInstalled()
    {
        if (Engine is null) return null;
        var shared = DirectoryOf(_runtimeRoot, Engine);
        if (Holds(shared)) return shared;
        var legacy = LegacyDirectoryOf(_speechRoot, Engine);
        if (!Holds(legacy)) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
            if (Directory.Exists(shared)) Directory.Delete(shared, recursive: true);
            Directory.Move(legacy, shared);
            return shared;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SpeechLog.Info($"The speech engine stays in its old folder for now: {exception.Message}");
            return legacy;
        }
    }

    private bool Holds(string directory)
        => File.Exists(Path.Combine(directory, Marker)) && Engine!.Libraries.All(library => File.Exists(Path.Combine(directory, library)));

    private static void Extract(string directory, PinnedFile package, SpeechEngineDefinition engine)
    {
        var path = Path.Combine(directory, package.Name);
        using (var archive = ZipFile.OpenRead(path))
        {
            foreach (var library in engine.Libraries)
            {
                var entry = archive.GetEntry($"runtimes/{engine.RuntimeIdentifier}/native/{library}") ?? throw new PinnedDownloadException("checksum");
                entry.ExtractToFile(Path.Combine(directory, library), overwrite: true);
            }
        }

        File.Delete(path);
    }

    // The download that runs: what it reports goes to everyone who waits for it, and who joins it is told first what is
    // on disk already.
    private sealed class Download : IPinnedDownloadProgress
    {
        private readonly Lock _gate = new();
        private readonly List<IPinnedDownloadProgress> _observers = [];
        private long _received;

        public Download(Func<Download, Task> run) => Completion = Task.Run(() => run(this));

        public Task Completion { get; }

        public void Add(IPinnedDownloadProgress observer)
        {
            lock (_gate)
            {
                if (_received > 0) observer.Received(_received);
                _observers.Add(observer);
            }
        }

        public void Remove(IPinnedDownloadProgress observer)
        {
            lock (_gate) _observers.Remove(observer);
        }

        public void Received(long bytes)
        {
            lock (_gate)
            {
                _received += bytes;
                foreach (var observer in _observers) observer.Received(bytes);
            }
        }

        public void Checking(PinnedFile file)
        {
            lock (_gate)
            {
                foreach (var observer in _observers) observer.Checking(file);
            }
        }
    }
}
