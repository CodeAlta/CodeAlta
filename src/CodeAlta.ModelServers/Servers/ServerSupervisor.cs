using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Core.IO;
using CodeAlta.ModelServers.LlamaCpp;

namespace CodeAlta.ModelServers.Servers;

/// <summary>What the host knows of a server it started: enough to take it back after its own restart.</summary>
public sealed record ServerRun
{
    /// <summary>Gets its process.</summary>
    public int ProcessId { get; init; }

    /// <summary>Gets when its process started, in UTC ticks.</summary>
    public long StartedTicks { get; init; }

    /// <summary>Gets its port.</summary>
    public int Port { get; init; }

    /// <summary>Gets the build it runs.</summary>
    public string BuildTag { get; init; } = "";

    /// <summary>Gets the folder of that build.</summary>
    public string BuildFolder { get; init; } = "";
}

/// <summary>
/// Runs one model server: starts <c>llama-server</c> so that its life is its own (no pipe to the host: its log is a file,
/// its standard streams go nowhere), takes it back after the host restarted (its process, start time, port and key
/// answer), restarts it after a failure with a back-off and gives up after a few, says why from its log, and reads where
/// its models stand. The router's model processes end with it.
/// </summary>
public sealed partial class ServerSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly string _folder;
    private readonly HttpClient _http;
    private readonly Func<string, string?, InstalledBuild?> _findBuild;
    private readonly Func<string, (string Model, string? Mmproj)?> _paths;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _disposed = new();
    private readonly Dictionary<string, double> _speeds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _contexts = new(StringComparer.Ordinal);
    private ServerDefinition _definition;
    private ServerStatus _status = ServerStatus.Stopped;
    private Process? _process;
    private RouterClient? _client;
    private Task? _monitor;
    private bool _stopping;
    private DateTimeOffset _lastFailure;

    /// <summary>Creates the supervisor of a server.</summary>
    /// <param name="folder">The folder of the server.</param>
    /// <param name="definition">What the person set.</param>
    /// <param name="http">The client of its HTTP calls.</param>
    /// <param name="findBuild">Finds the build of a backend (and a build tag, or null for the current one).</param>
    /// <param name="paths">Finds the files of a model on disk.</param>
    public ServerSupervisor(string folder, ServerDefinition definition, HttpClient http, Func<string, string?, InstalledBuild?> findBuild,
        Func<string, (string Model, string? Mmproj)?> paths)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _folder = folder;
        _definition = definition;
        _http = http;
        _findBuild = findBuild;
        _paths = paths;
        Directory.CreateDirectory(Path.Combine(_folder, "logs"));
        Directory.CreateDirectory(Path.Combine(_folder, "cache"));
        _contexts = ReadContexts();
    }

    /// <summary>Raised when the server changed.</summary>
    public event Action? Changed;

    /// <summary>Gets what the person set.</summary>
    public ServerDefinition Definition
    {
        get { lock (_gate) return _definition; }
    }

    /// <summary>Gets where it stands.</summary>
    public ServerStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>Gets the client of the running server, or null.</summary>
    public RouterClient? Client
    {
        get { lock (_gate) return _client; }
    }

    /// <summary>Gets the folder of the build it runs, or null.</summary>
    public string? BuildFolder => ReadRun()?.BuildFolder;

    /// <summary>Gets the path of its log.</summary>
    public string LogPath => Path.Combine(_folder, "logs", "server.log");

    /// <summary>Gets its key, made at its first start.</summary>
    public string Key
    {
        get
        {
            var path = Path.Combine(_folder, "keys");
            if (File.Exists(path) && File.ReadAllLines(path).FirstOrDefault(static line => line.Length > 0 && !line.StartsWith('#')) is { } existing) return existing.Trim();
            var key = "sk-codealta-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            SafeFile.WriteAllText(path, "# The key of this model server, read by CodeAlta only.\n" + key + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return key;
        }
    }

    /// <summary>Changes what the person set; a running server restarts to take it.</summary>
    /// <param name="definition">The new settings.</param>
    /// <param name="cancellationToken">Ends the restart.</param>
    /// <returns>A task that ends once it is applied.</returns>
    public async Task UpdateAsync(ServerDefinition definition, CancellationToken cancellationToken)
    {
        bool running;
        lock (_gate)
        {
            _definition = definition;
            running = _process is not null;
        }

        if (running) await RestartAsync(cancellationToken).ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Takes back a server the host started before its restart, when it still runs with its key.</summary>
    /// <param name="cancellationToken">Ends the check.</param>
    /// <returns>Whether it was taken back.</returns>
    public async Task<bool> AdoptAsync(CancellationToken cancellationToken)
    {
        if (ReadRun() is not { } run) return false;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(run.ProcessId);
            if (Math.Abs(process.StartTime.ToUniversalTime().Ticks - run.StartedTicks) > TimeSpan.FromSeconds(2).Ticks) { process.Dispose(); process = null; }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process?.Dispose();
            process = null;
        }

        if (process is null)
        {
            DeleteRun();
            return false;
        }

        var client = new RouterClient(_http, run.Port, Key);
        if (!await client.AcceptsKeyAsync(cancellationToken).ConfigureAwait(false))
        {
            process.Dispose();
            DeleteRun();
            return false;
        }

        lock (_gate)
        {
            _process = process;
            _client = client;
            _status = _status with { State = "ready", ProcessId = process.Id, Address = client.Address, BuildTag = run.BuildTag, StartedAt = new DateTimeOffset(run.StartedTicks, TimeSpan.Zero), Reason = null };
            _monitor = MonitorAsync(process, _disposed.Token);
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Starts the server.</summary>
    /// <param name="cancellationToken">Ends the wait for its start.</param>
    /// <returns>A task that ends once its process started.</returns>
    /// <exception cref="ModelHostException">Its build is not installed, or it does not start.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ServerDefinition definition;
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            definition = _definition;
            _stopping = false;
        }

        ServerArguments.Validate(definition);
        var build = _findBuild(definition.Backend, definition.Build)
            ?? throw new ModelHostException("no_build", definition.Build is null
                ? $"No {definition.Backend} build of llama.cpp is installed on this machine: install one in the llama.cpp tab."
                : $"The build {definition.Build} ({definition.Backend}) is not installed.");
        if (definition.Kind == ServerKinds.Several && build.Build is > 0 and < ServerArguments.MinimumRouterBuild)
            throw new ModelHostException("too_old", $"{build.Tag} is too old for a server of several models: use b{ServerArguments.MinimumRouterBuild} or newer.");
        var presetPath = Path.Combine(_folder, "preset.ini");
        var (preset, missing) = ServerArguments.Preset(definition, _paths);
        if (definition.Kind == ServerKinds.Several)
        {
            if (missing.Count == definition.Models.Count && missing.Count > 0)
                throw new ModelHostException("missing_model", $"None of the models of {definition.Name} is on this machine yet: {string.Join(", ", missing)}.");
            SafeFile.WriteAllText(presetPath, preset);
        }

        RotateLog();
        var arguments = ServerArguments.CommandLine(definition, build.Build, Path.Combine(_folder, "keys"), presetPath, LogPath, _paths);
        _ = Key; // made before the server reads it
        var program = LlamaBuilds.ServerProgram(build);
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = build.Folder }
            // The standard streams go nowhere through a shell that becomes the server, so that its life does not hang on
            // a pipe of the host (a write to a closed pipe would end it).
            : new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = build.Folder, ArgumentList = { "-c", "exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1", program } };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Only the server's own settings: no stray key, cache, token or options of the person's shell.
        foreach (var name in start.Environment.Keys.Where(static name => name.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase)
                     || name.StartsWith("HF_", StringComparison.OrdinalIgnoreCase) || name is "HUGGINGFACE_HUB_CACHE" or "MODEL_ENDPOINT").ToList())
            start.Environment.Remove(name);
        start.Environment["LLAMA_CACHE"] = Path.Combine(_folder, "cache");
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new ModelHostException("cannot_start", "The server did not start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new ModelHostException("cannot_start", $"llama-server does not start: {exception.Message}");
        }

        var client = new RouterClient(_http, definition.Port, Key);
        var run = new ServerRun { ProcessId = process.Id, StartedTicks = SafeStartTicks(process), Port = definition.Port, BuildTag = build.Tag, BuildFolder = build.Folder };
        SafeFile.WriteAllText(Path.Combine(_folder, "run.json"), JsonSerializer.Serialize(run, ModelServersJsonContext.Default.ServerRun));
        lock (_gate)
        {
            _process = process;
            _client = client;
            _status = new ServerStatus("starting", process.Id, client.Address, build.Tag, missing.Count > 0 ? $"Not on this machine yet: {string.Join(", ", missing)}." : null,
                DateTimeOffset.UtcNow, _status.Restarts, StatusOfModels(definition, null));
            _monitor = MonitorAsync(process, _disposed.Token);
        }

        Changed?.Invoke();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Stops the server: unloads its models, then ends its process and its model processes.</summary>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>A task that ends once it stopped.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Process? process;
        RouterClient? client;
        lock (_gate)
        {
            _stopping = true;
            process = _process;
            client = _client;
        }

        if (process is not null)
        {
            // llama.cpp only handles Ctrl+C on Windows: unloading first lets the model processes end cleanly.
            if (client is not null && Definition.Kind == ServerKinds.Several)
            {
                foreach (var model in Status.Models.Where(static model => model.State is "loaded" or "loading" or "sleeping"))
                    await client.UnloadAsync(model.Model, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
            }
        }

        DeleteRun();
        lock (_gate)
        {
            _process?.Dispose();
            _process = null;
            _client = null;
            _status = ServerStatus.Stopped with { Models = StatusOfModels(_definition, null) };
        }

        Changed?.Invoke();
    }

    /// <summary>Restarts the server.</summary>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>A task that ends once it started again.</returns>
    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _status = _status with { Restarts = 0 };
        }

        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until the server answers, at most for a time.</summary>
    /// <param name="timeout">How long.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>Whether it is ready.</returns>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            var status = Status;
            if (status.State == "ready") return true;
            if (status.State is "failed" or "stopped") return false;
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        return Status.State == "ready";
    }

    /// <summary>Reads the end of the log.</summary>
    /// <param name="lines">How many lines.</param>
    /// <returns>The lines.</returns>
    public IReadOnlyList<string> ReadLog(int lines)
    {
        try
        {
            if (!File.Exists(LogPath)) return [];
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - Math.Max(1, lines) * 400L);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var all = text.Split('\n').Select(static line => line.TrimEnd('\r')).Where(static line => line.Length > 0).ToList();
            if (start > 0 && all.Count > 0) all.RemoveAt(0);
            return all.TakeLast(lines).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task MonitorAsync(Process process, CancellationToken cancellationToken)
    {
        await Task.Yield();
        var tick = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(tick == 0 ? TimeSpan.FromMilliseconds(300) : PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            tick++;
            bool exited;
            try { exited = process.HasExited; }
            catch (InvalidOperationException) { exited = true; }
            lock (_gate)
            {
                if (_process != process) return; // stopped or replaced
            }

            if (exited)
            {
                await OnExitedAsync(process, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await PollAsync(tick, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task PollAsync(int tick, CancellationToken cancellationToken)
    {
        RouterClient? client;
        ServerDefinition definition;
        lock (_gate)
        {
            client = _client;
            definition = _definition;
        }

        if (client is null) return;
        var health = await client.HealthAsync(cancellationToken).ConfigureAwait(false);
        var previous = Status;
        if (health == 0)
        {
            if (previous.State == "ready") Set(previous with { State = "starting", Reason = "The server does not answer." });
            return;
        }

        var models = new List<ServerModelStatus>();
        var readDetails = tick % 3 == 1;
        if (definition.Kind == ServerKinds.Several)
        {
            var listed = await client.ModelsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var model in definition.Models)
            {
                var entry = listed.FirstOrDefault(item => item.Model == model.Model);
                var old = previous.Models.FirstOrDefault(item => item.Model == model.Model);
                if (entry.Model is null)
                {
                    models.Add(new ServerModelStatus(model.Model, _paths(model.Model) is null ? "missing" : "unloaded", null, Context(model.Model), null, 0, 0, null, false));
                    continue;
                }

                int busy = old?.BusySlots ?? 0, total = old?.TotalSlots ?? 0;
                int? context = Context(model.Model);
                if (entry.State == "loaded" && readDetails)
                {
                    (busy, total, var slotContext) = await client.SlotsAsync(model.Model, cancellationToken).ConfigureAwait(false);
                    if (slotContext is { } n) context = Remember(model.Model, n);
                    if (await client.TokensPerSecondAsync(model.Model, cancellationToken).ConfigureAwait(false) is { } speed) lock (_gate) _speeds[model.Model] = speed;
                }
                else if (entry.State != "loaded")
                {
                    busy = 0;
                }

                models.Add(new ServerModelStatus(model.Model, entry.State, entry.Progress, context, entry.TrainedContext, busy, total, Speed(model.Model), entry.Vision));
            }
        }
        else
        {
            var model = definition.Models[0];
            var old = previous.Models.FirstOrDefault();
            var state = health == 200 ? "loaded" : "loading";
            int busy = old?.BusySlots ?? 0, total = old?.TotalSlots ?? 0;
            var context = Context(model.Model);
            if (health == 200 && readDetails)
            {
                (busy, total, var slotContext) = await client.SlotsAsync(null, cancellationToken).ConfigureAwait(false);
                if (slotContext is { } n) context = Remember(model.Model, n);
                if (await client.TokensPerSecondAsync(null, cancellationToken).ConfigureAwait(false) is { } speed) lock (_gate) _speeds[model.Model] = speed;
            }

            models.Add(new ServerModelStatus(model.Model, state, null, context, null, busy, total, Speed(model.Model), _paths(model.Model)?.Mmproj is not null && model.Vision));
        }

        var ready = definition.Kind == ServerKinds.Several ? health == 200 : health is 200 or 503;
        var reason = health == 503 && definition.Kind == ServerKinds.One ? "Loading the model."
            : models.Any(static model => model.State == "failed") ? ReasonFromLog() ?? "A model failed to load: read the log."
            : null;
        Set(previous with { State = ready ? "ready" : "starting", Models = models, Reason = reason, Restarts = ready && previous.State != "ready" ? previous.Restarts : previous.Restarts });
    }

    private async Task OnExitedAsync(Process process, CancellationToken cancellationToken)
    {
        int code;
        try { code = process.ExitCode; }
        catch (InvalidOperationException) { code = -1; }
        bool stopping;
        lock (_gate)
        {
            stopping = _stopping;
            _process = null;
            _client = null;
        }

        process.Dispose();
        if (stopping) return;
        var reason = ReasonFromLog() ?? $"llama-server ended (exit code {code}).";
        var now = DateTimeOffset.UtcNow;
        var restarts = now - _lastFailure > TimeSpan.FromMinutes(10) ? 0 : Status.Restarts;
        _lastFailure = now;
        if (restarts >= Backoff.Length)
        {
            DeleteRun();
            Set(Status with { State = "failed", ProcessId = null, Address = null, Reason = reason, Restarts = restarts });
            return;
        }

        Set(Status with { State = "starting", ProcessId = null, Reason = $"{reason} It starts again in {Backoff[restarts].TotalSeconds:0} s.", Restarts = restarts + 1 });
        try
        {
            await Task.Delay(Backoff[restarts], cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_stopping) return;
            }

            await StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ModelHostException exception)
        {
            Set(Status with { State = "failed", Reason = exception.Message });
        }
    }

    /// <summary>Says that the server waits for something before it can start (its build, a model being downloaded).</summary>
    /// <param name="reason">What it waits for, for people.</param>
    public void MarkWaiting(string reason) => Set(ServerStatus.Stopped with { State = "waiting", Reason = reason, Models = StatusOfModels(Definition, null) });

    /// <summary>Reads why a server failed from the end of its log, in words a person acts on.</summary>
    /// <returns>The reason, or null.</returns>
    public string? ReasonFromLog()
    {
        var lines = ReadLog(80);
        if (lines.Any(static line => OutOfMemoryPattern().IsMatch(line)))
            return "Not enough GPU memory: lower the context, choose a smaller quantization, or free the GPU.";
        if (lines.Any(static line => line.Contains("address already in use", StringComparison.OrdinalIgnoreCase) || line.Contains("couldn't bind", StringComparison.OrdinalIgnoreCase)))
            return $"Port {Definition.Port} is used by another program: choose another port.";
        if (lines.LastOrDefault(static line => line.Contains("error", StringComparison.OrdinalIgnoreCase) && !line.Contains("error_count", StringComparison.Ordinal)) is { } error)
            return "llama-server: " + (error.Length > 300 ? error[..300] : error).Trim();
        return null;
    }

    private void Set(ServerStatus status)
    {
        bool changed;
        lock (_gate)
        {
            changed = _status != status && !(_status.State == status.State && _status.Models.SequenceEqual(status.Models) && _status.Reason == status.Reason && _status.ProcessId == status.ProcessId);
            _status = status;
        }

        if (changed) Changed?.Invoke();
    }

    private List<ServerModelStatus> StatusOfModels(ServerDefinition definition, string? state)
        => definition.Models.Select(model => new ServerModelStatus(model.Model, _paths(model.Model) is null ? "missing" : state ?? "unloaded", null, Context(model.Model), null, 0, 0, null,
            _paths(model.Model)?.Mmproj is not null && model.Vision)).ToList();

    private double? Speed(string model)
    {
        lock (_gate) return _speeds.TryGetValue(model, out var speed) ? speed : null;
    }

    private int? Context(string model)
    {
        lock (_gate) return _contexts.TryGetValue(model, out var context) ? context : null;
    }

    // The context a model had when it last loaded is what a provider sizes its compaction with, also while it is unloaded.
    private int Remember(string model, int context)
    {
        lock (_gate)
        {
            if (_contexts.GetValueOrDefault(model) == context) return context;
            _contexts[model] = context;
            try
            {
                SafeFile.WriteAllText(Path.Combine(_folder, "contexts.json"),
                    string.Join('\n', _contexts.Select(static pair => pair.Key + "\t" + pair.Value.ToString(CultureInfo.InvariantCulture))));
            }
            catch (IOException)
            {
            }

            return context;
        }
    }

    private Dictionary<string, int> ReadContexts()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = Path.Combine(_folder, "contexts.json");
        try
        {
            if (!File.Exists(path)) return result;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length == 2 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var context)) result[parts[0]] = context;
            }
        }
        catch (IOException)
        {
        }

        return result;
    }

    private void RotateLog()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var previous = Path.Combine(_folder, "logs", "server.1.log");
            File.Move(LogPath, previous, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private ServerRun? ReadRun()
    {
        try
        {
            var path = Path.Combine(_folder, "run.json");
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), ModelServersJsonContext.Default.ServerRun) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void DeleteRun()
    {
        try { File.Delete(Path.Combine(_folder, "run.json")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static long SafeStartTicks(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { return DateTime.UtcNow.Ticks; }
    }

    /// <summary>Stops watching the server without stopping it: it goes on, and the next host takes it back.</summary>
    /// <returns>A task that ends once the watch stopped.</returns>
    public async ValueTask DetachAsync()
    {
        Task? monitor;
        lock (_gate)
        {
            _stopping = true;
            monitor = _monitor;
        }

        await _disposed.CancelAsync().ConfigureAwait(false);
        if (monitor is not null)
        {
            try { await monitor.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DetachAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _process?.Dispose();
            _process = null;
        }

        _disposed.Dispose();
    }

    [GeneratedRegex(@"out of memory|failed to allocate|cudaMalloc failed|hipErrorOutOfMemory|ErrorOutOfDeviceMemory|unable to allocate", RegexOptions.IgnoreCase)]
    private static partial Regex OutOfMemoryPattern();
}
