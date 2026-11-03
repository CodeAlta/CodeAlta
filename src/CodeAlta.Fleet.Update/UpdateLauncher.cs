using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CodeAlta.Fleet.Update;

/// <summary>The options of an <see cref="UpdateLauncher"/>.</summary>
public sealed record UpdateLauncherOptions
{
    /// <summary>Gets the folder of versions.</summary>
    public required UpdateVersionStore Store { get; init; }

    /// <summary>Gets the product.</summary>
    public required UpdateProduct Product { get; init; }

    /// <summary>Gets the arguments each version is started with (<c>run</c> and its options, as the service gave them).</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Gets how the installed entry point is started again, when the pointer goes back to it.</summary>
    public required UpdateCommand Installed { get; init; }

    /// <summary>Gets the version of the installed entry point, which the versions report; null when unknown.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>Gets what starts the processes.</summary>
    public IUpdateProcessRunner Runner { get; init; } = new UpdateProcessRunner();

    /// <summary>Gets how long a version on trial has to confirm that it works.</summary>
    public TimeSpan TrialLimit { get; init; } = UpdateLauncher.DefaultTrialLimit;

    /// <summary>Gets how many starts a version on trial gets (a restart of the machine during a trial is not its failure).</summary>
    public int MaxTrialStarts { get; init; } = 2;

    /// <summary>Gets how often the state is read while a version is on trial.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets how long a version that did not confirm is given to stop before it is killed.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(75);

    /// <summary>Gets the pause before a version is started again after it asked for a restart.</summary>
    public TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the <c>DOTNET_ROOT</c> given to the versions, so that their app host finds the runtime this process runs on; null for none.</summary>
    public string? DotnetRoot { get; init; } = UpdateLauncher.FindDotnetRoot();

    /// <summary>Gets what the launcher reports (it has no log of its own); null for nothing.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// Gets what restores the backup of a trial rolled back (the server's database); null for nothing. It throws when it
    /// cannot: the launcher then starts no version, and tries again at its next start.
    /// </summary>
    public Func<UpdateTrial, CancellationToken, Task>? RolledBack { get; init; }

    /// <summary>
    /// Gets what copies the data a new version may change (the server's database) before a switch that waited is taken
    /// between two versions, as <see cref="UpdateVersionStore.TakePending(UpdateProduct, Func{UpdateVersion, string?})"/>
    /// describes; null when the product needs no copy.
    /// </summary>
    public Func<UpdateVersion, string?>? Backup { get; init; }

    /// <summary>Gets whether the launcher handles SIGTERM and SIGINT itself (true for a process; false in tests).</summary>
    public bool HandleSignals { get; init; } = true;

    /// <summary>Gets what stops the launcher: the version that runs is asked to stop, and the launcher ends with its exit code.</summary>
    public CancellationToken Stop { get; init; }
}

/// <summary>
/// The launcher hop: the installed entry point (what the service starts) starts the version the pointer selects as a
/// child process, passes it its arguments, and ends with its exit code. It watches a version on trial: one that exits
/// before it confirmed, or does not confirm within <see cref="UpdateLauncherOptions.TrialLimit"/>, is stopped, marked
/// bad and rolled back, and the previous version is started. A version that asks for a restart
/// (<see cref="RestartExitCode"/>, after it selected another version) is followed by the newly selected one: the process
/// the service manager started never exits during an update, so no service manager has to restart anything.
/// </summary>
public sealed class UpdateLauncher
{
    /// <summary>The variable that tells a version it runs under a launcher (the launcher's process id).</summary>
    public const string SupervisorVariable = "CODEALTA_UPDATE_SUPERVISOR";

    /// <summary>The variable that tells a version the version of the installed entry point.</summary>
    public const string InstalledVersionVariable = "CODEALTA_UPDATE_INSTALLED_VERSION";

    /// <summary>The exit code of a version that stopped to let its launcher start the newly selected version (EX_TEMPFAIL).</summary>
    public const int RestartExitCode = 75;

    /// <summary>
    /// The exit code of a launcher that rolled a version back but could not restore the backup taken before it (EX_IOERR):
    /// it starts no version on data that version may not read, and restores first at its next start.
    /// </summary>
    public const int RestoreFailedExitCode = 74;

    /// <summary>How long a version on trial has to confirm, by default.</summary>
    public static readonly TimeSpan DefaultTrialLimit = TimeSpan.FromMinutes(2);

    private readonly UpdateLauncherOptions _options;
    private int _stopping;
    private IUpdateProcess? _child;

    /// <summary>Initializes a new instance of the <see cref="UpdateLauncher"/> class.</summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public UpdateLauncher(UpdateLauncherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Gets whether this process runs under a launcher (<see cref="SupervisorVariable"/>).</summary>
    public static bool IsSupervised => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SupervisorVariable));

    /// <summary>Gets the version of the installed entry point a launcher passed to this process; null when none did.</summary>
    public static string? SupervisorInstalledVersion => Environment.GetEnvironmentVariable(InstalledVersionVariable) is { Length: > 0 } version ? version : null;

    /// <summary>
    /// Gets whether a process must hand over to the selected version: it is the installed entry point (not a folder of
    /// versions), no launcher runs it, and the pointer selects a version.
    /// </summary>
    /// <param name="store">The folder of versions.</param>
    /// <param name="baseDirectory">The folder of this process's application (<see cref="AppContext.BaseDirectory"/>).</param>
    /// <returns>Whether to start a launcher.</returns>
    public static bool ShouldHandOver(UpdateVersionStore store, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        return !IsSupervised && !store.Contains(baseDirectory) && store.Read() is { } state && (state.Current is not null || state.RollingBack is not null);
    }

    /// <summary>
    /// Finds the folder of the .NET installation this process runs on (the parent of <c>shared/Microsoft.NETCore.App</c>),
    /// given to the versions as <c>DOTNET_ROOT</c>, so that their app host finds the same runtime.
    /// </summary>
    /// <returns>The folder, or null when this process does not run on a shared runtime.</returns>
    public static string? FindDotnetRoot()
    {
        var runtime = new DirectoryInfo(Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory()));
        var root = runtime.Parent?.Parent?.Parent;
        return root is not null && Directory.Exists(Path.Combine(root.FullName, "host", "fxr")) ? root.FullName : null;
    }

    /// <summary>Gets how a staged version starts: its app host, or <c>dotnet exec</c> with its assembly when it has none.</summary>
    /// <param name="store">The folder of versions.</param>
    /// <param name="product">The product.</param>
    /// <param name="version">The version.</param>
    /// <param name="dotnetRoot">The .NET installation for <c>dotnet exec</c>; null for the <c>dotnet</c> of the <c>PATH</c>.</param>
    /// <returns>The command, or null when the version is not staged.</returns>
    public static UpdateCommand? CommandFor(UpdateVersionStore store, UpdateProduct product, UpdateVersion version, string? dotnetRoot)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(product);
        var folder = store.VersionPath(version);
        var assembly = Path.Combine(folder, product.AssemblyFileName);
        if (!File.Exists(assembly)) return null;
        var host = Path.Combine(folder, product.AppHostFileName(OperatingSystem.IsWindows()));
        if (File.Exists(host)) return new UpdateCommand(host, []);
        var muxer = dotnetRoot is null ? null : Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return new UpdateCommand(muxer is not null && File.Exists(muxer) ? muxer : "dotnet", ["exec", assembly]);
    }

    /// <summary>Starts the selected version and follows it until it ends without asking for a restart.</summary>
    /// <returns>The exit code of the last version started.</returns>
    public async Task<int> RunAsync()
    {
        using var signals = _options.HandleSignals ? RegisterSignals() : null;
        using var stop = _options.Stop.Register(() => RequestStop(forward: true));
        var store = _options.Store;
        while (true)
        {
            // A rollback whose backup is not restored yet (a crash or a failure since) is finished before anything starts.
            if (store.Read().RollingBack is { } rollback && !await RestoreAsync(rollback).ConfigureAwait(false)) return RestoreFailedExitCode;

            // Between two versions nothing runs: a switch that waits is taken at once.
            if (store.TakePending(_options.Product, _options.Backup) is { } taken) Log($"Switching to {taken}, which waited for the next start.");
            var state = store.Read();
            var trial = state.Trial is { } selected && UpdateVersion.TryParse(selected.Version, out var trialVersion) && UpdateVersionStore.Same(state.Current, trialVersion)
                ? selected : null;
            if (trial is not null && trial.Starts >= _options.MaxTrialStarts)
            {
                RollBack($"it did not confirm that it works after {trial.Starts} start(s)");
                continue;
            }

            UpdateCommand command;
            string label;
            if (state.Current is null)
            {
                command = _options.Installed;
                label = $"the installed {_options.Product.AssemblyName}{(_options.InstalledVersion is { } installed ? " " + installed : "")}";
            }
            else if (UpdateVersion.TryParse(state.Current, out var current) && CommandFor(store, _options.Product, current, _options.DotnetRoot) is { } found)
            {
                command = found;
                label = $"{_options.Product.AssemblyName} {current}{(trial is null ? "" : " (on trial)")}";
            }
            else
            {
                // The folder of the selected version is gone (removed by hand): a version on trial is rolled back, a
                // confirmed one leaves the pointer to the installed entry point.
                if (trial is not null)
                {
                    RollBack($"its folder {store.Root}{Path.DirectorySeparatorChar}{state.Current} is missing");
                }
                else
                {
                    Log($"The selected version {state.Current} is missing from {store.Root}: the installed entry point runs.");
                    store.Change(s => s with { Current = null, Previous = null });
                }

                continue;
            }

            if (trial is not null) store.CountTrialStart();
            Log($"Starting {label}.");
            var started = Stopwatch.StartNew();
            using var process = Start(command, label, trial is not null);
            if (process is null) continue;
            Volatile.Write(ref _child, process);
            if (Volatile.Read(ref _stopping) == 1) process.RequestStop();
            int exitCode;
            try
            {
                if (trial is null)
                {
                    exitCode = await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    var (code, rolledBack) = await WatchTrialAsync(process, UpdateVersion.Parse(trial.Version)).ConfigureAwait(false);
                    if (rolledBack) continue;
                    exitCode = code;
                }
            }
            finally
            {
                Volatile.Write(ref _child, null);
            }

            if (exitCode == RestartExitCode && Volatile.Read(ref _stopping) == 0)
            {
                Log($"{_options.Product.AssemblyName} {state.Current ?? _options.InstalledVersion ?? ""} stopped to switch versions after {started.Elapsed.TotalSeconds:0} s.");
                await Task.Delay(_options.RestartDelay).ConfigureAwait(false);
                continue;
            }

            return exitCode;
        }
    }

    // Follows a version on trial: confirmed (it goes on as any version), exited or silent too long (rolled back).
    private async Task<(int ExitCode, bool RolledBack)> WatchTrialAsync(IUpdateProcess process, UpdateVersion version)
    {
        var store = _options.Store;
        var exited = process.WaitForExitAsync(CancellationToken.None);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            await Task.WhenAny(exited, Task.Delay(_options.PollInterval)).ConfigureAwait(false);
            var state = store.Read();
            var onTrial = state.Trial is { } trial && UpdateVersionStore.Same(trial.Version, version);
            if (!onTrial)
            {
                // Confirmed (or the state changed by another hand): the version goes on as any other.
                if (UpdateVersionStore.Same(state.Current, version)) Log($"{_options.Product.AssemblyName} {version} confirmed that it works.");
                return (await exited.ConfigureAwait(false), false);
            }

            if (exited.IsCompleted)
            {
                var code = await exited.ConfigureAwait(false);
                // A stop asked during the trial is not the version's failure: it is tried again at the next start.
                if (Volatile.Read(ref _stopping) == 1) return (code, false);
                RollBack($"it exited with code {code.ToString(CultureInfo.InvariantCulture)} before it confirmed that it works");
                return (code, true);
            }

            if (deadline.Elapsed < _options.TrialLimit || Volatile.Read(ref _stopping) == 1) continue;
            Log($"{_options.Product.AssemblyName} {version} did not confirm within {_options.TrialLimit.TotalSeconds:0} s: it is stopped.");
            await StopAsync(process, exited).ConfigureAwait(false);
            RollBack($"it did not confirm that it works within {_options.TrialLimit.TotalSeconds:0} s");
            return (await exited.ConfigureAwait(false), true);
        }
    }

    private async Task StopAsync(IUpdateProcess process, Task<int> exited)
    {
        // Windows has no graceful request to send: the version is killed; its sessions are resumable as after a crash.
        var grace = process.RequestStop() ? _options.StopGrace : TimeSpan.Zero;
        if (grace > TimeSpan.Zero && await Task.WhenAny(exited, Task.Delay(grace)).ConfigureAwait(false) == exited) return;
        process.Kill();
        await exited.ConfigureAwait(false);
    }

    // Starts a version. One on trial that cannot be started (its app host corrupt, of another architecture) fails its
    // trial as one that crashes: it is rolled back, and null tells the loop to go on with the previous version.
    private IUpdateProcess? Start(UpdateCommand command, string label, bool onTrial)
    {
        try
        {
            return _options.Runner.Start(command, _options.Arguments, ChildEnvironment());
        }
        catch (Exception exception) when (onTrial && exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            Log($"{label} could not be started: {exception.Message}");
            RollBack($"it could not be started: {exception.Message}");
            return null;
        }
    }

    // Moves the pointer back and marks the trial bad; its backup, if any, is restored at the top of the loop, before
    // anything starts (RestoreAsync), so that a crash in between finds it recorded.
    private void RollBack(string reason)
    {
        var store = _options.Store;
        var trial = store.RollBack(reason);
        if (trial is null) return;
        var state = store.Read();
        var next = state.Current is { } current ? current : "the installed entry point";
        Log($"{_options.Product.AssemblyName} {trial.Version} is rolled back and marked bad: {reason}. "
            + (state.RollingBack is null ? $"{char.ToUpperInvariant(next[0])}{next[1..]} runs again." : $"The backup taken before it is restored, then {next} runs again."));
        if (state.RollingBack is null) store.Prune();
    }

    // Restores the backup of a rollback. When it cannot, nothing starts: the previous version would run on data the
    // version rolled back may have migrated beyond what it reads. The rollback stays recorded for the next start.
    private async Task<bool> RestoreAsync(UpdateRollback rollback)
    {
        var store = _options.Store;
        var trial = rollback.Trial;
        string error;
        if (_options.RolledBack is not { } restore)
        {
            error = "this launcher cannot restore it";
        }
        else
        {
            try
            {
                await restore(trial, CancellationToken.None).ConfigureAwait(false);
                store.CompleteRollback(trial);
                store.Prune();
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }
        }

        try
        {
            store.RecordRestoreFailure(error);
        }
        catch (UpdateException exception)
        {
            Log(exception.Message);
        }

        Log($"{_options.Product.AssemblyName} {trial.Version} was rolled back, but the backup taken before it ({trial.Backup}) could not be restored: {error}. "
            + "No version is started on data it may not read: fix the cause and start again, the restore is tried first.");
        return false;
    }

    private Dictionary<string, string?> ChildEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SupervisorVariable] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            [InstalledVersionVariable] = _options.InstalledVersion,
        };
        // The app host of a version finds the runtime of this process, whatever the service manager gave as variables.
        if (_options.DotnetRoot is { } root && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT"))) environment["DOTNET_ROOT"] = root;
        return environment;
    }

    private void RequestStop(bool forward)
    {
        Interlocked.Exchange(ref _stopping, 1);
        if (forward) Volatile.Read(ref _child)?.RequestStop();
    }

    // SIGTERM (a service manager's stop) is passed to the version; SIGINT (Ctrl+C in the terminal) reaches it already,
    // as part of the same console or process group. Either way the launcher ends once the version exits.
    private Signals RegisterSignals()
    {
        var registrations = new List<PosixSignalRegistration>();
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
        {
            try
            {
                registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    RequestStop(forward: context.Signal == PosixSignal.SIGTERM);
                }));
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        return new Signals(registrations);
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    private sealed class Signals(List<PosixSignalRegistration> registrations) : IDisposable
    {
        public void Dispose()
        {
            foreach (var registration in registrations) registration.Dispose();
        }
    }
}
