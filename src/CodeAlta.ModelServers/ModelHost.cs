using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Core.IO;
using CodeAlta.ModelServers.Downloads;
using CodeAlta.ModelServers.Hardware;
using CodeAlta.ModelServers.HuggingFace;
using CodeAlta.ModelServers.LlamaCpp;
using CodeAlta.ModelServers.Servers;
using CodeAlta.ModelServers.Store;

namespace CodeAlta.ModelServers;

/// <summary>Where a model host keeps its files, and how it reaches the network.</summary>
public sealed record ModelHostOptions
{
    /// <summary>Gets the folder shared by the instances of a profile: builds, downloads and settings (<c>~/.alta/llama</c>).</summary>
    public required string SharedRoot { get; init; }

    /// <summary>Gets the folder of this instance: its servers (<c>&lt;state root&gt;/llama</c>).</summary>
    public required string InstanceRoot { get; init; }

    /// <summary>Gets the first port of the servers.</summary>
    public int FirstPort { get; init; } = 9931;

    /// <summary>Gets the token of Hugging Face (the <c>HF_TOKEN</c> secret of the machine), read at each call.</summary>
    public Func<string?> HuggingFaceToken { get; init; } = static () => null;

    /// <summary>Gets the handler of the HTTP calls to the network, or null for the system's.</summary>
    public HttpMessageHandler? NetworkHandler { get; init; }

    /// <summary>Gets the probe of the hardware, or null for this machine's.</summary>
    public HardwareProbe? Probe { get; init; }

    /// <summary>Gets the address of the GitHub API.</summary>
    public string GitHubApi { get; init; } = "https://api.github.com";

    /// <summary>Gets the address of the Hugging Face Hub.</summary>
    public string HuggingFaceEndpoint { get; init; } = HuggingFaceClient.DefaultEndpoint;

    /// <summary>Gets the clock.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Gets whether its servers keep running when the host stops (a node does; a desktop stops those not kept).</summary>
    public bool ServersOutliveHost { get; init; }
}

/// <summary>
/// The model host of a machine: its GPUs, the llama.cpp builds it installs and keeps up to date, the models in its
/// Hugging Face cache and their downloads, and the model servers it runs. CodeAlta Desktop and CodeAlta Node each compose
/// one; everything they show or do (the page, the node API, <c>alta llama</c>) goes through it.
/// </summary>
public sealed partial class ModelHost : IAsyncDisposable
{
    private readonly ModelHostOptions _options;
    private readonly HttpClient _network;
    private readonly HttpClient _noRedirect;
    private readonly HttpClient _local;
    private readonly HardwareProbe _probe;
    private readonly LlamaReleases _releases;
    private readonly HuggingFaceClient _hub;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ServerSupervisor> _servers = new(StringComparer.Ordinal);
    private readonly List<HostTask> _tasks = [];
    private readonly CancellationTokenSource _disposed = new();
    private HardwareInventory _hardware = new(HardwareProbe.CurrentOs, HardwareProbe.CurrentArchitecture, null, null, [], new MachineLibraries(null, null, null, false, false, null), []);
    private LlamaRelease? _latest;
    private DateTimeOffset _latestAt;
    private ModelHostSettings _settings;
    private ModelStore _store;
    private ModelDownloads _downloads;
    private IReadOnlyList<StoredModel> _models = [];
    private IReadOnlyList<UnmanagedServer> _unmanaged = [];
    private long _revision;
    private Task? _background;

    /// <summary>Creates the model host; <see cref="StartAsync"/> reads the machine and starts its servers.</summary>
    /// <param name="options">Where it keeps its files.</param>
    public ModelHost(ModelHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _network = options.NetworkHandler is null ? new HttpClient() : new HttpClient(options.NetworkHandler, disposeHandler: false);
        _network.Timeout = Timeout.InfiniteTimeSpan;
        _network.DefaultRequestHeaders.UserAgent.ParseAdd("CodeAlta/1.0");
        _noRedirect = options.NetworkHandler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            : new HttpClient(options.NetworkHandler, disposeHandler: false);
        _noRedirect.DefaultRequestHeaders.UserAgent.ParseAdd("CodeAlta/1.0");
        _local = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _probe = options.Probe ?? new HardwareProbe();
        _releases = new LlamaReleases(_network, options.GitHubApi);
        _hub = new HuggingFaceClient(_network, _noRedirect, options.HuggingFaceToken, options.HuggingFaceEndpoint);
        Builds = new LlamaBuilds(Path.Combine(options.SharedRoot, "builds"), _network);
        _settings = ReadSettings();
        _store = new ModelStore(_settings.ModelsFolder ?? ModelStore.DefaultRoot());
        _downloads = CreateDownloads(_store);
    }

    /// <summary>Raised when anything changed; consumers read <see cref="GetStatus"/> again.</summary>
    public event Action? Changed;

    /// <summary>Raised once when a download ends.</summary>
    public event Action<DownloadInfo>? DownloadEnded;

    /// <summary>Raised once when an install or an update ends.</summary>
    public event Action<HostTask>? TaskEnded;

    /// <summary>Gets the builds.</summary>
    public LlamaBuilds Builds { get; }

    /// <summary>Gets the client of the Hub.</summary>
    public HuggingFaceClient Hub => _hub;

    /// <summary>Gets the store of the models.</summary>
    public ModelStore Store
    {
        get { lock (_gate) return _store; }
    }

    /// <summary>Reads the machine, takes back its running servers and starts those that should run.</summary>
    /// <param name="cancellationToken">Ends the start.</param>
    /// <returns>A task that ends once the host runs.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshHardwareAsync(cancellationToken).ConfigureAwait(false);
        RescanModels();
        foreach (var folder in Directory.Exists(ServersRoot) ? Directory.EnumerateDirectories(ServersRoot) : [])
        {
            var path = Path.Combine(folder, "server.json");
            if (!File.Exists(path)) continue;
            try
            {
                var definition = JsonSerializer.Deserialize(File.ReadAllText(path), ModelServersJsonContext.Default.ServerDefinition);
                if (definition is not null) AddSupervisor(definition);
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var supervisor in Supervisors())
        {
            if (await supervisor.AdoptAsync(cancellationToken).ConfigureAwait(false)) continue;
            if (supervisor.Definition.Enabled) await TryStartAsync(supervisor, cancellationToken).ConfigureAwait(false);
        }

        _downloads.Start();
        _background = Task.Run(() => BackgroundAsync(_disposed.Token), CancellationToken.None);
        NotifyChanged();
    }

    /// <summary>Reads everything the host says now.</summary>
    /// <returns>The status.</returns>
    public ModelHostStatus GetStatus()
    {
        var builds = Builds.List();
        HardwareInventory hardware;
        LlamaRelease? latest;
        ModelHostSettings settings;
        IReadOnlyList<StoredModel> models;
        ModelStore store;
        List<HostTask> tasks;
        lock (_gate)
        {
            hardware = _hardware;
            latest = _latest;
            settings = _settings;
            models = _models;
            store = _store;
            tasks = [.. _tasks];
        }

        var candidates = BuildSelector.Candidates(hardware, latest);
        var downloads = _downloads.List();
        var downloading = downloads.Where(static download => download.State is "queued" or "downloading" or "checking" or "paused")
            .ToDictionary(static download => download.Model, static download => download, StringComparer.Ordinal);
        var shown = models.Select(model => downloading.ContainsKey(model.Id) ? model with { State = "downloading" } : model).ToList();
        var servers = Supervisors().Select(static supervisor => new ServerView(supervisor.Definition, supervisor.Status)).OrderBy(static view => view.Definition.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new ModelHostStatus(hardware, builds, candidates, latest, settings, ReadPlanned(), ReadHistory(), store.Root, models.Sum(static model => model.FreedBytes)
            + SharedBytes(models), store.FreeBytes(), shown, downloads, servers, _unmanaged, Warnings(), tasks, Interlocked.Read(ref _revision));
    }

    /// <summary>Reads the hardware again.</summary>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>What the machine has.</returns>
    public async Task<HardwareInventory> RefreshHardwareAsync(CancellationToken cancellationToken)
    {
        var hardware = await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _hardware = hardware;
        }

        NotifyChanged();
        return hardware;
    }

    /// <summary>Reads the build the channel of the machine offers now (kept for six hours unless forced).</summary>
    /// <param name="force">Whether to read GitHub again.</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>The build, or null when GitHub could not be read.</returns>
    public async Task<LlamaRelease?> CheckLatestAsync(bool force, CancellationToken cancellationToken)
    {
        UpdatePolicy policy;
        lock (_gate)
        {
            if (!force && _latest is not null && _options.Time.GetUtcNow() - _latestAt < TimeSpan.FromHours(6)) return _latest;
            policy = _settings.Updates;
        }

        try
        {
            var release = await ResolveChannelAsync(policy, null, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _latest = release;
                _latestAt = _options.Time.GetUtcNow();
            }

            NotifyChanged();
            return release;
        }
        catch (ModelHostException)
        {
            return _latest;
        }
    }

    private async Task<LlamaRelease> ResolveChannelAsync(UpdatePolicy policy, string? build, CancellationToken cancellationToken)
    {
        if (build is not null) return await _releases.GetBuildAsync(build, cancellationToken).ConfigureAwait(false);
        if (policy.Channel == "pinned" && policy.PinnedBuild is { } pinned) return await _releases.GetBuildAsync(pinned, cancellationToken).ConfigureAwait(false);
        if (policy.Channel == "every")
        {
            var day = _options.Time.GetUtcNow() - TimeSpan.FromDays(1);
            var builds = await _releases.ListBuildsAsync(60, cancellationToken).ConfigureAwait(false);
            return builds.FirstOrDefault(release => release.PublishedAt <= day) ?? builds.LastOrDefault()
                ?? throw new ModelHostException("not_found", "GitHub lists no build of llama.cpp.");
        }

        return await _releases.GetStableAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs builds of llama.cpp: the backends named, or those the hardware calls for.</summary>
    /// <param name="backends">The backends, or empty for the recommended ones.</param>
    /// <param name="build">A build, or null for the channel's.</param>
    /// <param name="requestedBy">Who asks.</param>
    /// <returns>The task that installs them, which runs on.</returns>
    /// <exception cref="ModelHostException">A backend is unknown.</exception>
    public HostTask InstallBuilds(IReadOnlyList<string> backends, string? build, string? requestedBy)
    {
        var task = AddTask("install", backends.Count == 0 ? "Install llama.cpp" : $"Install llama.cpp ({string.Join(", ", backends)})", requestedBy);
        _ = Task.Run(async () =>
        {
            try
            {
                var release = await ResolveChannelAsync(Settings.Updates, build, _disposed.Token).ConfigureAwait(false);
                HardwareInventory hardware;
                lock (_gate) hardware = _hardware;
                var candidates = BuildSelector.Candidates(hardware, release);
                var chosen = backends.Count > 0 ? backends : BuildSelector.Recommended(hardware, candidates);
                var installed = new List<string>();
                var failures = new List<string>();
                foreach (var backend in chosen)
                {
                    var candidate = candidates.FirstOrDefault(c => c.Backend == backend);
                    var assets = BuildSelector.AssetsFor(release, hardware.Os, hardware.Architecture, backend, candidate);
                    UpdateTask(task.Id, t => t with { Total = t.Total + assets.Sum(static asset => asset.Size), Message = $"Downloading {release.Tag} ({backend})…" });
                    try
                    {
                        var makeCurrent = Builds.Find(backend, null) is null;
                        await Builds.InstallAsync(release, backend, assets, makeCurrent, bytes => UpdateTask(task.Id, t => t with { Done = Math.Max(0, t.Done + bytes) }), _disposed.Token)
                            .ConfigureAwait(false);
                        installed.Add($"{release.Tag} ({backend})");
                    }
                    catch (ModelHostException exception)
                    {
                        failures.Add($"{backend}: {exception.Message}");
                    }
                }

                EndTask(task.Id, failures.Count == 0 || installed.Count > 0 ? "done" : "failed",
                    string.Join(" ", new[] { installed.Count > 0 ? "Installed " + string.Join(", ", installed) + "." : null, failures.Count > 0 ? string.Join(" ", failures) : null }.OfType<string>()));
            }
            catch (Exception exception) when (exception is ModelHostException or IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException)
            {
                EndTask(task.Id, "failed", exception.Message);
            }
            catch (OperationCanceledException)
            {
                EndTask(task.Id, "failed", "Stopped.");
            }
        });
        return task;
    }

    /// <summary>Removes a build that no server runs.</summary>
    /// <param name="backend">The backend.</param>
    /// <param name="tag">The build.</param>
    /// <returns>The bytes freed.</returns>
    /// <exception cref="ModelHostException">A server runs it.</exception>
    public long RemoveBuild(string backend, string tag)
    {
        if (Builds.Find(backend, tag) is { } build && Supervisors().Any(supervisor => supervisor.Status.State != "stopped" && supervisor.BuildFolder == build.Folder))
            throw new ModelHostException("in_use", $"A server runs {tag} ({backend}): stop it first.");
        var freed = Builds.Remove(backend, tag);
        NotifyChanged();
        return freed;
    }

    /// <summary>Makes an installed build the one its backend runs, and restarts the servers of that backend on it.</summary>
    /// <param name="backend">The backend.</param>
    /// <param name="tag">The build.</param>
    /// <param name="cancellationToken">Ends the restarts.</param>
    /// <returns>A task that ends once the servers run it.</returns>
    /// <exception cref="ModelHostException">It is not installed.</exception>
    public async Task UseBuildAsync(string backend, string tag, CancellationToken cancellationToken)
    {
        if (Builds.Find(backend, tag) is null) throw new ModelHostException("not_found", $"{tag} ({backend}) is not installed.");
        Builds.MakeCurrent(backend, tag);
        foreach (var supervisor in Supervisors().Where(supervisor => supervisor.Definition.Backend == backend && supervisor.Definition.Build is null && supervisor.Status.State != "stopped"))
            await supervisor.RestartAsync(cancellationToken).ConfigureAwait(false);
        NotifyChanged();
    }

    /// <summary>Changes the settings of the machine.</summary>
    /// <param name="settings">The new settings.</param>
    /// <exception cref="ModelHostException">A value is not valid.</exception>
    public void SetSettings(ModelHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var policy = settings.Updates;
        if (policy.Channel is not ("stable" or "every" or "pinned")) throw new ModelHostException("invalid", "The channel is stable, every or pinned.");
        if (policy.Channel == "pinned" && policy.PinnedBuild is null) throw new ModelHostException("invalid", "A pinned channel names its build.");
        if (policy.Schedule is not ("manual" or "daily" or "weekly")) throw new ModelHostException("invalid", "Updates are manual, daily or weekly.");
        if (!TimeOnly.TryParseExact(policy.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new ModelHostException("invalid", "The time is HH:mm.");
        if (!Enum.TryParse<DayOfWeek>(policy.Day, ignoreCase: true, out _)) throw new ModelHostException("invalid", "The day is a day of the week.");
        if (settings.ModelsFolder is { } folder && !Path.IsPathFullyQualified(folder)) throw new ModelHostException("invalid", "The folder of the models is a full path.");
        ModelDownloads? previous = null;
        lock (_gate)
        {
            var moved = !string.Equals(settings.ModelsFolder ?? "", _settings.ModelsFolder ?? "", StringComparison.Ordinal);
            _settings = settings with { DownloadsAtOnce = Math.Clamp(settings.DownloadsAtOnce, 1, 4) };
            if (moved)
            {
                previous = _downloads;
                _store = new ModelStore(settings.ModelsFolder ?? ModelStore.DefaultRoot());
                _downloads = CreateDownloads(_store);
            }

            _latestAt = DateTimeOffset.MinValue;
        }

        Directory.CreateDirectory(_options.SharedRoot);
        SafeFile.WriteAllText(Path.Combine(_options.SharedRoot, "settings.json"), JsonSerializer.Serialize(Settings, ModelServersJsonContext.Default.ModelHostSettings));
        if (previous is not null)
        {
            _ = previous.DisposeAsync().AsTask();
            _downloads.Start();
        }

        RescanModels();
    }

    /// <summary>Gets the settings.</summary>
    public ModelHostSettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    /// <summary>Lists the models on disk again.</summary>
    /// <returns>The models.</returns>
    public IReadOnlyList<StoredModel> RescanModels()
    {
        var models = Store.Scan();
        lock (_gate)
        {
            _models = models;
        }

        NotifyChanged();
        return models;
    }

    /// <summary>Asks for a model to be downloaded.</summary>
    /// <param name="model">The model, <c>org/repo:TAG</c>.</param>
    /// <param name="requestedBy">Who asks.</param>
    /// <returns>Its download.</returns>
    /// <exception cref="ModelHostException">The model is not one.</exception>
    public DownloadInfo Download(string model, string? requestedBy) => _downloads.Request(model, requestedBy);

    /// <summary>Pauses, resumes or cancels a download.</summary>
    /// <param name="id">The download.</param>
    /// <param name="action"><c>pause</c>, <c>resume</c> or <c>cancel</c>.</param>
    /// <returns>The download.</returns>
    /// <exception cref="ModelHostException">It is not found, or the action is not one.</exception>
    public DownloadInfo ControlDownload(string id, string action)
        => (action switch
        {
            "pause" => _downloads.Pause(id),
            "resume" => _downloads.Resume(id),
            "cancel" => _downloads.Cancel(id),
            "clear" => ClearEnded(),
            _ => throw new ModelHostException("invalid", "A download is paused, resumed or cancelled."),
        }) ?? throw new ModelHostException("not_found", $"There is no download {id}.");

    private DownloadInfo? ClearEnded()
    {
        _downloads.ClearEnded();
        return new DownloadInfo("", "", "cleared", 0, 0, 0, null, null, 0, DateTimeOffset.UtcNow, null);
    }

    /// <summary>Removes a model from the disk.</summary>
    /// <param name="model">The model.</param>
    /// <param name="stopServers">Whether to stop the running servers that serve it first.</param>
    /// <param name="cancellationToken">Ends the stops.</param>
    /// <returns>The bytes freed.</returns>
    /// <exception cref="ModelHostException">A running server serves it and <paramref name="stopServers"/> is false, or a file is in use.</exception>
    public async Task<long> RemoveModelAsync(string model, bool stopServers, CancellationToken cancellationToken)
    {
        var users = Supervisors().Where(supervisor => supervisor.Status.State != "stopped" && supervisor.Definition.Models.Any(m => string.Equals(m.Model, model, StringComparison.OrdinalIgnoreCase))).ToList();
        if (users.Count > 0 && !stopServers)
            throw new ModelHostException("in_use", $"{string.Join(", ", users.Select(static s => s.Definition.Name))} serves {model}: stop it, or remove with --stop.");
        foreach (var supervisor in users)
        {
            if (supervisor.Definition.Kind == ServerKinds.Several && supervisor.Client is { } client && supervisor.Definition.Models.Count > 1)
                await client.UnloadAsync(supervisor.RouterName(model), cancellationToken).ConfigureAwait(false);
            else
                await supervisor.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        // A model process lets its files go a moment after it ends.
        long freed = 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                freed = Store.Remove(model);
                break;
            }
            catch (ModelHostException exception) when (exception.Code == "in_use" && attempt < 10 && users.Count > 0)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        RescanModels();
        return freed;
    }

    /// <summary>Lists the quantizations of a repository with their size, and whether each is on this machine.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>Its details and quantizations.</returns>
    /// <exception cref="ModelHostException">The repository is not there.</exception>
    public async Task<(HubModelDetails Details, IReadOnlyList<QuantFiles> Quantizations)> InspectRepoAsync(string repo, CancellationToken cancellationToken)
    {
        var details = await _hub.GetModelAsync(repo, cancellationToken).ConfigureAwait(false);
        return (details, GgufNames.Quantizations(details.Files));
    }

    private ModelDownloads CreateDownloads(ModelStore store)
    {
        var downloads = new ModelDownloads(store, _hub, new RangeDownloader(_noRedirect), () => Settings.DownloadsAtOnce,
            Path.Combine(_options.SharedRoot, "downloads.json"), _options.Time);
        downloads.Changed += NotifyChanged;
        downloads.Ended += download =>
        {
            if (download.State == "done") RescanModels();
            DownloadEnded?.Invoke(download);
            if (download.State == "done") _ = StartWaitingServersAsync(download.Model);
        };
        return downloads;
    }

    // A server whose models were not on disk yet starts once one arrives.
    private async Task StartWaitingServersAsync(string model)
    {
        foreach (var supervisor in Supervisors().Where(supervisor => supervisor.Definition.Enabled && supervisor.Definition.Models.Any(m => m.Model == model)))
        {
            try
            {
                if (supervisor.Status.State is "stopped" or "failed" or "waiting") await supervisor.StartAsync(_disposed.Token).ConfigureAwait(false);
                else if (supervisor.Definition.Kind == ServerKinds.Several) await supervisor.UpdateAsync(supervisor.Definition, _disposed.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ModelHostException or IOException or OperationCanceledException)
            {
            }
        }
    }

    private async Task BackgroundAsync(CancellationToken cancellationToken)
    {
        var lastUnmanaged = DateTimeOffset.MinValue;
        await CheckLatestAsync(force: false, cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _options.Time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunScheduledUpdatesAsync(cancellationToken).ConfigureAwait(false);
                if (_options.Time.GetUtcNow() - lastUnmanaged > TimeSpan.FromMinutes(2))
                {
                    lastUnmanaged = _options.Time.GetUtcNow();
                    var found = FindUnmanaged();
                    if (!found.SequenceEqual(_unmanaged))
                    {
                        _unmanaged = found;
                        NotifyChanged();
                    }

                    await CheckLatestAsync(force: false, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is ModelHostException or IOException or HttpRequestException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private List<UnmanagedServer> FindUnmanaged()
    {
        var ours = Supervisors().Select(static supervisor => supervisor.Status.ProcessId).OfType<int>().ToHashSet();
        var result = new List<UnmanagedServer>();
        foreach (var process in Process.GetProcessesByName("llama-server").Concat(Process.GetProcessesByName("llama")))
        {
            using (process)
            {
                if (ours.Contains(process.Id)) continue;
                var command = CommandLineOf(process.Id);
                // A model process of one of CodeAlta's routers is not another server.
                if (command is not null && command.Contains("--alias", StringComparison.Ordinal) && command.Contains("--port 0", StringComparison.Ordinal)) continue;
                if (command is not null && ours.Count > 0 && Supervisors().Any(supervisor => command.Contains(supervisor.Definition.Id, StringComparison.Ordinal))) continue;
                int? port = command is not null && PortPattern().Match(command) is { Success: true } match ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
                if (command is not null && command.Contains("--log-colors off", StringComparison.Ordinal) && command.Contains("--alias", StringComparison.Ordinal)) continue;
                result.Add(new UnmanagedServer(process.Id, command ?? process.ProcessName, port));
            }
        }

        return result;
    }

    private static string? CommandLineOf(int processId)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var raw = File.ReadAllText($"/proc/{processId}/cmdline");
                return raw.Replace('\0', ' ').Trim();
            }

            if (OperatingSystem.IsWindows())
            {
                using var process = Process.GetProcessById(processId);
                return process.MainModule?.FileName;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
        }

        return null;
    }

    private List<string> Warnings()
    {
        var warnings = new List<string>();
        foreach (var path in ConfigIniPaths().Where(File.Exists))
            warnings.Add($"{path} applies to every llama.cpp server of this machine: read it, or remove it.");
        return warnings;
    }

    private static IEnumerable<string> ConfigIniPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "llama.cpp", "config.ini");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "llama.cpp", "config.ini");
            yield break;
        }

        yield return "/etc/llama.cpp/config.ini";
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        yield return Path.Combine(config, "llama.cpp", "config.ini");
    }

    private static long SharedBytes(IReadOnlyList<StoredModel> models)
        => models.SelectMany(static model => model.Files.Where(static file => file.Shared).Select(file => (model.Repo, file.Path, file.Size)))
            .DistinctBy(static file => (file.Repo, file.Path)).Sum(static file => file.Size);

    private ModelHostSettings ReadSettings()
    {
        var path = Path.Combine(_options.SharedRoot, "settings.json");
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize(File.ReadAllText(path), ModelServersJsonContext.Default.ModelHostSettings) ?? new ModelHostSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new ModelHostSettings();
    }

    private HostTask AddTask(string kind, string title, string? requestedBy)
    {
        var task = new HostTask("task-" + Guid.NewGuid().ToString("N")[..8], kind, title, "running", 0, 0, null, _options.Time.GetUtcNow(), null, requestedBy);
        lock (_gate)
        {
            _tasks.RemoveAll(existing => existing.EndedAt is { } ended && _options.Time.GetUtcNow() - ended > TimeSpan.FromHours(1));
            _tasks.Add(task);
        }

        NotifyChanged();
        return task;
    }

    private void UpdateTask(string id, Func<HostTask, HostTask> change)
    {
        lock (_gate)
        {
            var index = _tasks.FindIndex(task => task.Id == id);
            if (index >= 0) _tasks[index] = change(_tasks[index]);
        }

        NotifyChanged();
    }

    private void EndTask(string id, string state, string? message)
    {
        HostTask? ended = null;
        lock (_gate)
        {
            var index = _tasks.FindIndex(task => task.Id == id);
            if (index >= 0) _tasks[index] = ended = _tasks[index] with { State = state, Message = message, EndedAt = _options.Time.GetUtcNow() };
        }

        NotifyChanged();
        if (ended is not null) TaskEnded?.Invoke(ended);
    }

    /// <summary>Raises <see cref="Changed"/> with a new revision.</summary>
    internal void NotifyChanged()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }

    [GeneratedRegex(@"--port[= ](\d+)")]
    private static partial Regex PortPattern();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _disposed.CancelAsync().ConfigureAwait(false);
        if (_background is not null)
        {
            try { await _background.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        await _downloads.DisposeAsync().ConfigureAwait(false);
        foreach (var supervisor in Supervisors())
        {
            if (_options.ServersOutliveHost || supervisor.Definition.KeepRunning) await supervisor.DetachAsync().ConfigureAwait(false);
            else
            {
                try { await supervisor.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or InvalidOperationException) { }
            }

            await supervisor.DisposeAsync().ConfigureAwait(false);
        }

        _network.Dispose();
        _noRedirect.Dispose();
        _local.Dispose();
        _disposed.Dispose();
    }
}
