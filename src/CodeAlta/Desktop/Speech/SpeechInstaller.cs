using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.NativeEngines;

namespace CodeAlta.Desktop.Speech;

/// <summary>Where a download of speech to prompt is.</summary>
/// <param name="State"><c>idle</c>, <c>downloading</c>, <c>installing</c>, <c>done</c>, <c>failed</c> or <c>canceled</c>.</param>
/// <param name="ModelId">The id of the model.</param>
/// <param name="ModelName">Its name, once known.</param>
/// <param name="ReceivedBytes">The bytes on disk, of <paramref name="TotalBytes"/>.</param>
/// <param name="TotalBytes">The bytes of the whole download, zero until it is known.</param>
/// <param name="Error">For <c>failed</c>: <c>network</c>, <c>not_found</c>, <c>checksum</c>, <c>unsupported_model</c>,
/// <c>busy_elsewhere</c>, <c>disk</c>, <c>unsupported_platform</c> or <c>load_failed</c>.</param>
internal sealed record SpeechInstallSnapshot(string State, string? ModelId, string? ModelName, long ReceivedBytes, long TotalBytes, string? Error)
{
    /// <summary>Gets the state before any download.</summary>
    public static SpeechInstallSnapshot Idle { get; } = new("idle", null, null, 0, 0, null);

    /// <summary>Gets whether a download runs.</summary>
    public bool IsRunning => State is "downloading" or "installing";
}

/// <summary>A model in the cache, as its <c>model.json</c> describes it.</summary>
internal sealed class SpeechModelManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Languages { get; set; } = "";
    public string License { get; set; } = "";
    /// <summary>Gets or sets the configuration value the model was installed for: a preset id or an address.</summary>
    public string Source { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Commit { get; set; } = "";
    public string Family { get; set; } = "";
    public List<SpeechManifestFile> Files { get; set; } = [];
}

/// <summary>A file of an installed model.</summary>
internal sealed class SpeechManifestFile
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>An installed model with what it takes on disk.</summary>
internal sealed record InstalledSpeechModel(SpeechModelManifest Manifest, string Directory, long Bytes);

internal sealed class HuggingFaceModel
{
    public string? Sha { get; set; }
}

internal sealed class HuggingFaceEntry
{
    public string? Type { get; set; }
    public string? Path { get; set; }
    public long Size { get; set; }
    public string? Oid { get; set; }
    public HuggingFaceLargeFile? Lfs { get; set; }
}

internal sealed class HuggingFaceLargeFile
{
    public string? Oid { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SpeechModelManifest))]
[JsonSerializable(typeof(HuggingFaceModel))]
[JsonSerializable(typeof(HuggingFaceEntry[]))]
[JsonSerializable(typeof(SpeechWorkerLoad))]
internal sealed partial class SpeechJsonContext : JsonSerializerContext;

/// <summary>A download that ends with a reason the user is told.</summary>
internal sealed class SpeechInstallException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Downloads what speech to prompt needs into the shared cache, one download at a time: the engine, the model
/// that finds speech, and a speech model. The download belongs to the application, not to a window: a
/// subscriber follows it and never ends it.
/// </summary>
/// <remarks>
/// A file is written as <c>name.part</c> in a <c>.partial</c> folder, resumed where it stopped, checked against
/// its hash and only then given its name. The folder gets its own name when its marker file is written, so a
/// folder under its name is complete. A lock in the <c>.partial</c> folder keeps the normal and the developer
/// instance from writing the same files.
/// </remarks>
internal sealed class SpeechInstaller
{
    private const string ModelMarker = "model.json";
    private const string VoiceDetectorMarker = "vad.json";
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly string _root;
    private readonly string _runtimeRoot;
    private readonly PinnedDownloader _downloads;
    private readonly SpeechEngineDefinition? _engine;
    private readonly SpeechModelDefinition _voiceDetector;
    private readonly Lock _gate = new();
    private readonly List<Action<SpeechInstallSnapshot>> _observers = [];
    private SpeechInstallSnapshot _snapshot = SpeechInstallSnapshot.Idle;
    private CancellationTokenSource? _cancellation;
    private Task _active = Task.CompletedTask;
    private bool _closed;

    /// <summary>Creates the installer of a cache folder.</summary>
    /// <param name="root">The folder of speech to prompt in the cache.</param>
    /// <param name="handler">The handler of its requests, or null for the network.</param>
    /// <param name="engine">The engine of this platform, or null when it has none.</param>
    /// <param name="voiceDetector">The one-file model that finds where speech starts and ends.</param>
    public SpeechInstaller(string root, HttpMessageHandler? handler, SpeechEngineDefinition? engine, SpeechModelDefinition voiceDetector)
        : this(root, Path.Combine(root, "runtime"), handler, engine, voiceDetector)
    {
    }

    /// <summary>Creates the installer of a cache folder whose engine is in a shared runtime folder.</summary>
    /// <param name="root">The folder of speech to prompt in the cache.</param>
    /// <param name="runtimeRoot">The folder of the native runtimes in the cache (<c>CatalogOptions.RuntimeRoot</c>).</param>
    /// <param name="handler">The handler of its requests, or null for the network.</param>
    /// <param name="engine">The engine of this platform, or null when it has none.</param>
    /// <param name="voiceDetector">The one-file model that finds where speech starts and ends.</param>
    public SpeechInstaller(string root, string runtimeRoot, HttpMessageHandler? handler, SpeechEngineDefinition? engine, SpeechModelDefinition voiceDetector)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(runtimeRoot);
        ArgumentNullException.ThrowIfNull(voiceDetector);
        _root = Path.GetFullPath(root);
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
        _downloads = new(handler, new("CodeAlta", DesktopCommandLine.Version));
        _engine = engine;
        _voiceDetector = voiceDetector;
    }

    /// <summary>Gets or sets what checks a model once it is on disk: an error code, or null when the engine loads it.</summary>
    public Func<InstalledSpeechModel, CancellationToken, Task<string?>>? Validate { get; set; }

    /// <summary>Gets the folder of the cache.</summary>
    public string Root => _root;

    /// <summary>Gets whether the engine exists for this platform.</summary>
    public bool IsSupported => _engine is not null;

    /// <summary>Gets the folder of the native libraries of the engine.</summary>
    public string EngineDirectory => _engine is null ? Path.Combine(_runtimeRoot, "sherpa-onnx", "none") : SherpaRuntime.Resolve(_runtimeRoot, _root, _engine);

    /// <summary>Gets the file of the model that finds where speech starts and ends.</summary>
    public string VoiceDetectorPath => Path.Combine(_root, "vad", _voiceDetector.Files[0].Name);

    /// <summary>Gets the libraries of the engine in the order they are loaded.</summary>
    public IReadOnlyList<string> EngineLibraries => _engine?.Libraries ?? [];

    /// <summary>Gets the latest state.</summary>
    public SpeechInstallSnapshot Current { get { lock (_gate) return _snapshot; } }

    private string ModelsRoot => Path.Combine(_root, "models");

    /// <summary>Gets whether the engine and the model that finds speech are on disk.</summary>
    public bool IsEngineInstalled
        => _engine is not null && SherpaRuntime.IsInstalled(EngineDirectory, _engine)
            && File.Exists(Path.Combine(_root, "vad", VoiceDetectorMarker)) && File.Exists(VoiceDetectorPath);

    /// <summary>Gets an installed model by its id, or null.</summary>
    public InstalledSpeechModel? Find(string? id)
    {
        if (!SpeechModelSource.IsSafeSegment(id)) return null;
        var directory = Path.Combine(ModelsRoot, id!);
        try
        {
            var marker = Path.Combine(directory, ModelMarker);
            if (!File.Exists(marker)) return null;
            var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(marker), SpeechJsonContext.Default.SpeechModelManifest);
            if (manifest is null || manifest.Id != id || manifest.Files.Count == 0) return null;
            long bytes = 0;
            foreach (var file in manifest.Files)
            {
                if (!SpeechModelSource.IsSafeSegment(file.Name)) return null;
                var info = new FileInfo(Path.Combine(directory, file.Name));
                if (!info.Exists || info.Length != file.Size) return null;
                bytes += info.Length;
            }

            return new(manifest, directory, bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Gets what a worker needs to load an installed model with the engine of this cache.</summary>
    public SpeechWorkerLoad LoadFor(InstalledSpeechModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new()
        {
            EngineDirectory = EngineDirectory,
            EngineLibraries = [.. EngineLibraries],
            ModelDirectory = model.Directory,
            Family = model.Manifest.Family,
            Files = model.Manifest.Files.Where(static file => file.Role.Length > 0).ToDictionary(static file => file.Role, static file => file.Name),
            VoiceDetector = VoiceDetectorPath,
            // Half the cores, within two and four: the recognizer is quick, and the computer does other work.
            Threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4),
        };
    }

    /// <summary>Gets the installed models, by name.</summary>
    public IReadOnlyList<InstalledSpeechModel> List()
    {
        try
        {
            if (!Directory.Exists(ModelsRoot)) return [];
            return [.. Directory.EnumerateDirectories(ModelsRoot).Select(directory => Find(Path.GetFileName(directory)))
                .OfType<InstalledSpeechModel>().OrderBy(static model => model.Manifest.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Follows the download. The observer gets the current state at once, then every change.</summary>
    public IDisposable Subscribe(Action<SpeechInstallSnapshot> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            _observers.Add(observer);
            observer(_snapshot);
        }

        return new Subscription(this, observer);
    }

    /// <summary>Starts the download of a model and of what it needs. False while another download runs.</summary>
    /// <param name="model">A preset id or the address of a Hugging Face repository.</param>
    public bool TryStart(string model)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        lock (_gate)
        {
            if (_closed || _snapshot.IsRunning) return false;
            var id = SpeechModelSource.ModelIdOf(model);
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            SetLocked(new("downloading", id, SpeechModelCatalog.FindPreset(model)?.Name, 0, 0, null));
            _active = Task.Run(() => RunAsync(model, id, cancellation));
            return true;
        }
    }

    /// <summary>Stops the running download. What is on disk stays, and the next download goes on from it.</summary>
    public void Cancel()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    /// <summary>Removes a model from the cache. False when it is being downloaded or could not be removed.</summary>
    public bool Delete(string id)
    {
        if (!SpeechModelSource.IsSafeSegment(id)) return false;
        lock (_gate)
        {
            if (_snapshot.IsRunning && _snapshot.ModelId == id) return false;
        }

        try
        {
            foreach (var directory in new[] { Path.Combine(ModelsRoot, id), Path.Combine(ModelsRoot, id + ".partial") })
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SpeechLog.Warn($"The speech model '{id}' could not be removed: {exception.Message}");
            return false;
        }
    }

    /// <summary>Stops the download and waits for it to end.</summary>
    public async Task CloseAsync()
    {
        Task active;
        lock (_gate)
        {
            _closed = true;
            _cancellation?.Cancel();
            active = _active;
        }

        await active.ConfigureAwait(false);
        _downloads.Dispose();
    }

    private async Task RunAsync(string model, string? id, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            if (_engine is null) throw new SpeechInstallException("unsupported_platform");
            if (id is null) throw new SpeechInstallException("unsupported_model");
            var definition = SpeechModelCatalog.FindPreset(model) ?? await ResolveAsync(model, token).ConfigureAwait(false);
            var installed = Find(definition.Id);
            var progress = new Progress(this, definition, (IsEngineInstalled ? 0 : _engine.Size + _voiceDetector.Size) + (installed is null ? definition.Size : 0));
            progress.Publish("downloading", force: true);
            if (!IsEngineInstalled)
            {
                await SherpaRuntime.InstallAsync(_downloads, _runtimeRoot, _engine, progress, () => progress.Publish("installing", force: true), token).ConfigureAwait(false);
                progress.Publish("downloading", force: true);
                await InstallFolderAsync(Path.Combine(_root, "vad"), VoiceDetectorMarker, [.. _voiceDetector.Files.Select(file => (file, _voiceDetector.AddressOf(file)))], null,
                    "{}"u8.ToArray(), progress, token).ConfigureAwait(false);
            }

            if (installed is null)
            {
                // The largest file last: the check of its hash, the longest, is then the last step shown.
                var files = definition.Files.OrderBy(static file => file.Size).Select(file => (file, definition.AddressOf(file))).ToArray();
                await InstallFolderAsync(Path.Combine(ModelsRoot, definition.Id), ModelMarker, files, null, Manifest(definition, model), progress, token).ConfigureAwait(false);
                installed = Find(definition.Id) ?? throw new SpeechInstallException("disk");
            }

            progress.Publish("installing", force: true);
            if (Validate is { } validate && await validate(installed, token).ConfigureAwait(false) is { } failure) throw new SpeechInstallException(failure);
            progress.Publish("done", force: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            End("canceled", null);
        }
        catch (SpeechInstallException exception)
        {
            SpeechLog.Warn($"The speech model '{id ?? model}' was not installed: {exception.Code}.");
            End("failed", exception.Code);
        }
        catch (PinnedDownloadException exception)
        {
            // A listing larger than a listing can be is not a model this installer knows.
            var code = exception.Code == "too_large" ? "unsupported_model" : exception.Code;
            SpeechLog.Warn($"The speech model '{id ?? model}' was not installed: {code}.");
            End("failed", code);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or OperationCanceledException)
        {
            SpeechLog.Warn($"The speech model '{id ?? model}' was not installed: {exception.GetType().Name}: {exception.Message}");
            End("failed", exception is HttpRequestException or OperationCanceledException ? "network" : "disk");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task<SpeechModelDefinition> ResolveAsync(string address, CancellationToken cancellationToken)
    {
        if (!SpeechModelSource.TryParse(address, out var repository)) throw new SpeechInstallException("unsupported_model");
        var escaped = string.Join('/', repository.Repository.Split('/').Select(Uri.EscapeDataString));
        var about = repository.Revision is null ? $"https://huggingface.co/api/models/{escaped}" : $"https://huggingface.co/api/models/{escaped}/revision/{Uri.EscapeDataString(repository.Revision)}";
        var info = JsonSerializer.Deserialize(await ReadAsync(about, cancellationToken).ConfigureAwait(false), SpeechJsonContext.Default.HuggingFaceModel);
        if (info?.Sha is not { Length: 40 } commit || !commit.All(char.IsAsciiHexDigitLower)) throw new SpeechInstallException("unsupported_model");
        var folder = repository.Folder.Length == 0 ? "" : "/" + string.Join('/', repository.Folder.Split('/').Select(Uri.EscapeDataString));
        var entries = JsonSerializer.Deserialize(await ReadAsync($"https://huggingface.co/api/models/{escaped}/tree/{commit}{folder}", cancellationToken).ConfigureAwait(false),
            SpeechJsonContext.Default.HuggingFaceEntryArray) ?? [];
        var files = entries.Where(static entry => entry is { Type: "file", Path: not null })
            .Select(static entry => new SpeechFile(entry.Path![(entry.Path!.LastIndexOf('/') + 1)..], "", entry.Size, Hash(entry.Lfs?.Oid, 64), entry.Lfs is null ? Hash(entry.Oid, 40) : null))
            .ToArray();
        return SpeechModelSource.Classify(address, repository, commit, files) ?? throw new SpeechInstallException("unsupported_model");

        static string? Hash(string? value, int length) => value is not null && value.Length == length && value.All(char.IsAsciiHexDigitLower) ? value : null;
    }

    private async Task<byte[]> ReadAsync(string address, CancellationToken cancellationToken)
        // A listing is a few kilobytes: a megabyte is not one.
        => await _downloads.ReadAsync(address, 1024 * 1024, cancellationToken).ConfigureAwait(false);

    private async Task InstallFolderAsync(string target, string marker, IReadOnlyList<(SpeechFile File, Uri Address)> files, Action<string>? finish,
        byte[] markerContent, Progress progress, CancellationToken cancellationToken)
    {
        await _downloads.InstallFolderAsync(target, marker, [.. files.Select(static entry => (ToPinned(entry.File), entry.Address))], (partial, _) =>
        {
            progress.Publish("installing", force: true);
            finish?.Invoke(partial);
            return Task.CompletedTask;
        }, markerContent, progress, cancellationToken).ConfigureAwait(false);
        progress.Publish("downloading", force: true);
    }

    /// <summary>Checks a file against the hash its source gives: SHA-256, or the SHA-1 git names a blob by.</summary>
    internal static Task<bool> VerifyAsync(string path, SpeechFile file, CancellationToken cancellationToken)
        => PinnedDownloader.VerifyAsync(path, ToPinned(file), cancellationToken);

    private static PinnedFile ToPinned(SpeechFile file) => new(file.Name, file.Size, file.Sha256, file.GitSha1);

    private static byte[] Manifest(SpeechModelDefinition definition, string source)
        => JsonSerializer.SerializeToUtf8Bytes(new SpeechModelManifest
        {
            Id = definition.Id, Name = definition.Name, Author = definition.Author, Languages = definition.Languages, License = definition.License,
            Source = source, Repository = definition.Repository, Commit = definition.Commit, Family = definition.Family.ToString(),
            Files = [.. definition.Files.Select(static file => new SpeechManifestFile { Name = file.Name, Role = file.Role, Size = file.Size })],
        }, SpeechJsonContext.Default.SpeechModelManifest);

    private void End(string state, string? error)
    {
        lock (_gate) SetLocked(_snapshot with { State = state, Error = error });
    }

    private void SetLocked(SpeechInstallSnapshot snapshot)
    {
        _snapshot = snapshot;
        foreach (var observer in _observers) observer(snapshot);
    }

    private sealed class Subscription(SpeechInstaller owner, Action<SpeechInstallSnapshot> observer) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._observers.Remove(observer);
        }
    }

    /// <summary>What is on disk of the whole download, published at most ten times a second.</summary>
    private sealed class Progress(SpeechInstaller owner, SpeechModelDefinition definition, long total) : IPinnedDownloadProgress
    {
        private long _received;
        private long _published;
        private string _state = "downloading";

        public void Received(long bytes)
        {
            _received += bytes;
            Publish(_state, force: false);
        }

        // The check of a large file takes a while: it is shown as installing.
        public void Checking(PinnedFile file)
        {
            if (file.Size >= 32 * 1024 * 1024) Publish("installing", force: true);
        }

        public void Publish(string state, bool force)
        {
            var now = Stopwatch.GetTimestamp();
            if (!force && Stopwatch.GetElapsedTime(_published, now) < TimeSpan.FromMilliseconds(100)) return;
            _published = now;
            _state = state is "done" ? _state : state;
            lock (owner._gate) owner.SetLocked(new(state, definition.Id, definition.Name, Math.Min(_received, total), total, null));
        }
    }
}
