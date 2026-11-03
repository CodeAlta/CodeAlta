using System.Globalization;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Update;

namespace CodeAlta.Server.Hosting;

/// <summary>What <c>GET /api/v1/server/update</c> reads: the versions of the server and its self-update.</summary>
internal sealed class ServerUpdateStatusSource(Func<FleetUpdateStatus?>? read)
{
    /// <summary>Reads the status: as the self-update reports it, or the running version alone.</summary>
    /// <returns>The status.</returns>
    public FleetUpdateStatus Read() => read?.Invoke() ?? new FleetUpdateStatus(ServerVersion.Current, null, null, null, null, null, [], null);
}

/// <summary>What a <see cref="ServerUpdates"/> reads and drives.</summary>
internal sealed record ServerUpdatesOptions
{
    /// <summary>Gets the folder of versions.</summary>
    public required UpdateVersionStore Store { get; init; }

    /// <summary>Gets the version that runs.</summary>
    public required UpdateVersion Running { get; init; }

    /// <summary>Gets the version of the installed entry point; null when unknown.</summary>
    public string? Installed { get; init; }

    /// <summary>Gets whether the server serves: it answers its own health request.</summary>
    public required Func<CancellationToken, Task<bool>> Healthy { get; init; }

    /// <summary>Copies the database before a switch to a version, and gives the path of the copy.</summary>
    public required Func<UpdateVersion, CancellationToken, Task<string>> Backup { get; init; }

    /// <summary>Stops the server to start the newly selected version.</summary>
    public required Action RequestRestart { get; init; }

    /// <summary>Gets how often the state is read.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets what the server logs.</summary>
    public Action<string> Log { get; init; } = static _ => { };

    /// <summary>Gets the settings of the updates (<c>update.json</c> of the data root); null to allow what an empty policy allows.</summary>
    public string? PolicyPath { get; init; }

    /// <summary>Gets the HTTP client of the feeds, for <see cref="ServerUpdates.ApplyAsync"/>; null for one of its own.</summary>
    public HttpClient? Http { get; init; }
}

/// <summary>
/// The self-update of a running server: a version on trial confirms itself once it serves; a switch asked for
/// (<c>altaserver update</c>, or the fleet through <see cref="ApplyAsync"/>) copies the database first
/// (<c>doc/fleet/server.md</c>, "Restore and upgrades"), selects the version with that backup, and stops the server, whose
/// launcher starts the new version. A rollback restores the backup.
/// </summary>
internal sealed class ServerUpdates : IAsyncDisposable
{
    private readonly ServerUpdatesOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private bool _confirmChecked;
    private int _restartRequested;

    /// <summary>Initializes a new instance of the <see cref="ServerUpdates"/> class; <see cref="Start"/> starts it.</summary>
    /// <param name="options">What it reads and drives.</param>
    public ServerUpdates(ServerUpdatesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Gets whether the server stopped (or stops) to start a newly selected version.</summary>
    public bool RestartRequested => Volatile.Read(ref _restartRequested) == 1;

    /// <summary>Starts reading the state of the versions.</summary>
    public void Start() => _loop = Task.Run(LoopAsync);

    /// <summary>Gets the versions of the server and where its self-update stands.</summary>
    /// <returns>The status.</returns>
    public FleetUpdateStatus Status() => UpdateStatusReport.Describe(_options.Store.Read(), _options.Running.ToString(), _options.Installed);

    /// <summary>
    /// Downloads, checks and stages a version, then asks this server to switch to it: what the coordinated update of the
    /// fleet calls once the nodes are up to date, with the hash a controller device signed.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="requestedBy">Who asks, recorded with the switch.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The staged version, or null when the server already runs the version asked for or the latest one.</returns>
    /// <exception cref="UpdateException">The feed is not allowed, the package does not match its hash, or the state cannot be written.</exception>
    public async Task<StagedUpdate?> ApplyAsync(UpdateRequest request, string requestedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requestedBy);
        using var ownHttp = _options.Http is null ? new HttpClient() : null;
        var updater = new SelfUpdater(new SelfUpdaterOptions
        {
            Product = UpdateProduct.Server, Store = _options.Store, Running = _options.Running, Http = _options.Http ?? ownHttp!,
            Policy = _options.PolicyPath is null ? UpdateSourcePolicy.From(new UpdateSourceSettings()) : UpdateSourcePolicy.Load(_options.PolicyPath),
        });
        var check = await updater.CheckAsync(request, cancellationToken).ConfigureAwait(false);
        if (check.Target is null) return null;
        var staged = await updater.StageAsync(check, request, cancellationToken).ConfigureAwait(false);
        _options.Store.RequestSwitch(staged.Version, request.Now, requestedBy);
        return staged;
    }

    /// <summary>Reads the state once: confirms a trial, or carries out a pending switch. The loop calls it; tests call it directly.</summary>
    /// <param name="cancellationToken">Cancels the health request and the backup.</param>
    /// <returns>A task that completes once the step is done.</returns>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        var store = _options.Store;
        var running = _options.Running;
        var state = store.Read();
        if (!_confirmChecked)
        {
            if (state.Trial is { } trial && UpdateVersionStore.Same(trial.Version, running))
            {
                // A version on trial takes no switch until it confirmed: its launcher rolls it back otherwise.
                if (!await _options.Healthy(cancellationToken).ConfigureAwait(false)) return;
                if (store.Confirm(running))
                {
                    _options.Log($"CodeAlta Server {running} serves: it confirmed its update.");
                    store.Prune();
                }
            }

            _confirmChecked = true;
            state = store.Read();
        }

        if (state.Pending is not { } pending || RestartRequested) return;
        if (!UpdateVersion.TryParse(pending.Version, out var target) || !store.IsStaged(target, UpdateProduct.Server))
        {
            store.CancelPending($"{pending.Version} is not staged in {store.Root}.");
            _options.Log($"The switch to {pending.Version} was dropped: it is not staged.");
            return;
        }

        if (target.Equals(running))
        {
            store.CancelPending(null);
            return;
        }

        store.SetPhase(target, UpdatePhases.BackingUp);
        string backup;
        try
        {
            backup = await _options.Backup(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            // Without a backup there is no way back from the migrations of the new version: no switch.
            store.CancelPending($"The database could not be copied before the switch: {exception.Message}");
            _options.Log($"The switch to {target} was dropped: the database could not be copied ({exception.Message}).");
            return;
        }

        store.Select(target, backup);
        _options.Log($"CodeAlta Server copied its database to {backup} and stops to start {target} on trial.");
        if (Interlocked.Exchange(ref _restartRequested, 1) == 0) _options.RequestRestart();
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested && !RestartRequested)
        {
            try
            {
                await TickAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (UpdateException exception)
            {
                _options.Log($"The self-update could not read or write its state: {exception.Message}");
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(_options.Interval, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Names the backup taken before a switch, and removes the older ones beyond the last few.</summary>
    /// <param name="paths">The files of the server.</param>
    /// <param name="target">The version switched to.</param>
    /// <param name="now">The time.</param>
    /// <returns>The path of the new backup.</returns>
    internal static string BackupPath(ServerPaths paths, UpdateVersion target, DateTimeOffset now)
    {
        Directory.CreateDirectory(paths.BackupsRoot);
        const int Kept = 5;
        foreach (var old in Directory.EnumerateFiles(paths.BackupsRoot, "before-*.sqlite3").OrderByDescending(File.GetLastWriteTimeUtc).Skip(Kept - 1))
        {
            try { File.Delete(old); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        return Path.Combine(paths.BackupsRoot, $"before-{target}-{ServerVersion.Current}-{now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.sqlite3");
    }

    /// <summary>
    /// Restores the backup a rollback brings back: the database the previous version had before the switch. What the new
    /// version wrote meanwhile is lost; nodes publish again what the server lacks.
    /// </summary>
    /// <param name="paths">The files of the server; no server runs on them.</param>
    /// <param name="backup">The backup.</param>
    /// <exception cref="IOException">The backup is missing, or the database cannot be replaced.</exception>
    internal static void RestoreBackup(ServerPaths paths, string backup)
    {
        if (!File.Exists(backup)) throw new IOException($"The backup {backup} is missing.");
        var staging = paths.DatabasePath + ".restore.tmp";
        File.Copy(backup, staging, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(paths.DatabasePath + suffix)) File.Delete(paths.DatabasePath + suffix);
        }

        File.Move(staging, paths.DatabasePath, overwrite: true);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
