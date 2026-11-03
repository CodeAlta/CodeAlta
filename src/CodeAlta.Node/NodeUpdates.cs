using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Update;
using CodeAlta.Hosting;

namespace CodeAlta.Node;

/// <summary>Where the self-update of a node keeps its versions and its settings, in the fleet folder of its state root.</summary>
internal static class NodeUpdatePaths
{
    /// <summary>Gets the folder of versions of a profile: <c>fleet/versions</c> of its state root.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The store of its versions.</returns>
    public static UpdateVersionStore Store(CodeAltaInstanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new UpdateVersionStore(Path.Combine(profile.StateRoot, "fleet", "versions"));
    }

    /// <summary>Gets the file of the update campaign of the fleet a node accepted: <c>fleet/update-campaign.json</c>.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The path.</returns>
    public static string CampaignPath(CodeAltaInstanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Path.Combine(profile.StateRoot, "fleet", "update-campaign.json");
    }

    /// <summary>Gets the settings of the updates of a profile: <c>fleet/update.json</c> (the feed, the allowed feeds).</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The path.</returns>
    public static string PolicyPath(CodeAltaInstanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Path.Combine(profile.StateRoot, "fleet", UpdateSourcePolicy.FileName);
    }
}

/// <summary>What a <see cref="NodeUpdates"/> reads and drives.</summary>
internal sealed record NodeUpdatesOptions
{
    /// <summary>Gets the folder of versions.</summary>
    public required UpdateVersionStore Store { get; init; }

    /// <summary>Gets the version that runs.</summary>
    public required UpdateVersion Running { get; init; }

    /// <summary>Gets the version of the installed entry point; null when unknown.</summary>
    public string? Installed { get; init; }

    /// <summary>Gets how many sessions work now.</summary>
    public required Func<int> SessionsAtWork { get; init; }

    /// <summary>Pauses or resumes the admission of new work.</summary>
    public required Action<bool> PauseAdmission { get; init; }

    /// <summary>Gets whether the node is healthy: it listens (when it should) and its connections to the servers it joined are up.</summary>
    public required Func<bool> Healthy { get; init; }

    /// <summary>Stops the node to start the newly selected version.</summary>
    public required Action RequestRestart { get; init; }

    /// <summary>Gets how often the state is read.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets how long new work is refused while the node waits for its sessions.</summary>
    public TimeSpan DrainBound { get; init; } = UpdateDrain.DefaultBound;

    /// <summary>Gets what the node logs.</summary>
    public Action<string> Log { get; init; } = static _ => { };

    /// <summary>Gets the HTTP client of the feeds, for <see cref="NodeUpdates.ApplyAsync"/>; null for one of its own.</summary>
    public HttpClient? Http { get; init; }

    /// <summary>Gets the settings of the updates (<c>fleet/update.json</c>); null to allow what an empty policy allows.</summary>
    public string? PolicyPath { get; init; }
}

/// <summary>
/// The self-update of a running node: a version on trial confirms itself once the node is healthy; a switch asked for
/// (<c>altanode update</c>, or the fleet through <see cref="ApplyAsync"/>) is carried out when the node is idle — new
/// work is refused while it drains, within a bound, then it waits for the next idle moment unless the switch is
/// <c>--now</c> — by selecting the version and stopping the node, whose launcher starts the new version.
/// </summary>
internal sealed class NodeUpdates : IAsyncDisposable
{
    private readonly NodeUpdatesOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private bool _confirmChecked;
    private UpdateDrain? _drain;
    private DateTimeOffset? _drainFor;
    private int _restartRequested;

    /// <summary>Initializes a new instance of the <see cref="NodeUpdates"/> class; <see cref="Start"/> starts it.</summary>
    /// <param name="options">What it reads and drives.</param>
    public NodeUpdates(NodeUpdatesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Gets whether the node stopped (or stops) to start a newly selected version.</summary>
    public bool RestartRequested => Volatile.Read(ref _restartRequested) == 1;

    /// <summary>Starts reading the state of the versions.</summary>
    public void Start() => _loop = Task.Run(LoopAsync);

    /// <summary>Gets the versions of the node and where its self-update stands.</summary>
    /// <returns>The status.</returns>
    public FleetUpdateStatus Status() => UpdateStatusReport.Describe(_options.Store.Read(), _options.Running.ToString(), _options.Installed);

    /// <summary>
    /// Downloads, checks and stages a version, then asks this node to switch to it when idle: what the coordinated update
    /// of the fleet calls, with the hash a controller device signed and <see cref="UpdateOrigin.Remote"/>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="requestedBy">Who asks, recorded with the switch.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The staged version, or null when the node already runs the version asked for or the latest one.</returns>
    /// <exception cref="UpdateException">
    /// The feed is not allowed, the package does not match its hash, the state cannot be written, or a switch another
    /// requester asked for waits (<see cref="UpdateErrorCodes.SwitchWaits"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; no switch is asked for then.</exception>
    public async Task<StagedUpdate?> ApplyAsync(UpdateRequest request, string requestedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requestedBy);
        using var ownHttp = _options.Http is null ? new HttpClient() : null;
        var updater = Updater(_options.Http ?? ownHttp!);
        var check = await updater.CheckAsync(request, cancellationToken).ConfigureAwait(false);
        if (check.Target is null) return null;
        var staged = await updater.StageAsync(check, request, cancellationToken).ConfigureAwait(false);
        // Published atomically, under the lock of the state: not once the campaign was cancelled (its withdrawal comes
        // after the cancellation), and never over a switch someone else asked for.
        UpdatePending? waiting = null;
        if (_options.Store.TryRequestSwitch(staged.Version, request.Now, requestedBy, pending =>
            {
                waiting = pending;
                return !cancellationToken.IsCancellationRequested && (pending is null || string.Equals(pending.RequestedBy, requestedBy, StringComparison.Ordinal));
            }))
            return staged;
        cancellationToken.ThrowIfCancellationRequested();
        throw new UpdateException(UpdateErrorCodes.SwitchWaits,
            $"A switch to {waiting?.Version} asked by {waiting?.RequestedBy ?? "another process"} waits on this machine: {requestedBy} does not replace it (altanode update decides).");
    }

    /// <summary>Reads a feed for what it offers this node, without installing anything (an automatic update of the fleet checks the latest version with it).</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the requests to the feed.</param>
    /// <returns>What the feed offers.</returns>
    /// <exception cref="UpdateException">The feed is not allowed or did not answer.</exception>
    public async Task<UpdateCheck> CheckAsync(UpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var ownHttp = _options.Http is null ? new HttpClient() : null;
        return await Updater(_options.Http ?? ownHttp!).CheckAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Withdraws the switch that waits only when a requester asked for it (a cancelled campaign of the fleet).</summary>
    /// <param name="reason">Why.</param>
    /// <param name="requestedBy">Who asked for the switch to withdraw.</param>
    /// <returns>Whether that switch waited and was withdrawn.</returns>
    /// <exception cref="UpdateException">The state cannot be written.</exception>
    public bool CancelPending(string reason, string requestedBy) => _options.Store.CancelPending(reason, requestedBy);

    /// <summary>Gets the settings of the updates of this node (<c>fleet/update.json</c>), read again.</summary>
    /// <returns>The policy.</returns>
    /// <exception cref="UpdateException">The file is not valid.</exception>
    public UpdateSourcePolicy Policy() => _options.PolicyPath is null ? UpdateSourcePolicy.From(new UpdateSourceSettings()) : UpdateSourcePolicy.Load(_options.PolicyPath);

    private SelfUpdater Updater(HttpClient http) => new(new SelfUpdaterOptions
    {
        Product = UpdateProduct.Node, Store = _options.Store, Running = _options.Running, Http = http, Policy = Policy(),
    });

    /// <summary>Reads the state once: confirms a trial, or moves a pending switch on. The loop calls it; tests call it directly.</summary>
    /// <param name="now">The time.</param>
    internal void Tick(DateTimeOffset now)
    {
        var store = _options.Store;
        var running = _options.Running;
        var state = store.Read();
        if (!_confirmChecked)
        {
            if (state.Trial is { } trial && UpdateVersionStore.Same(trial.Version, running))
            {
                // A version on trial takes no switch until it confirmed: its launcher rolls it back otherwise.
                if (!_options.Healthy()) return;
                if (store.Confirm(running))
                {
                    _options.Log($"CodeAlta Node {running} is healthy: it confirmed its update.");
                    store.Prune();
                }
            }

            _confirmChecked = true;
            state = store.Read();
        }

        if (state.Pending is not { } pending)
        {
            if (_drain is not null)
            {
                // The switch was withdrawn: work is taken again.
                _drain = null;
                _drainFor = null;
                _options.PauseAdmission(false);
            }

            return;
        }

        if (!UpdateVersion.TryParse(pending.Version, out var target) || !store.IsStaged(target, UpdateProduct.Node))
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

        if (_drain is null || _drainFor != pending.RequestedAt)
        {
            _drain = new UpdateDrain(pending.Now, _options.DrainBound);
            _drainFor = pending.RequestedAt;
            _options.Log($"Switching to {target}{(pending.Now ? " now" : " when idle")}.");
        }

        var atWork = _options.SessionsAtWork();
        var step = _drain.Step(atWork, now);
        _options.PauseAdmission(step.PauseAdmission);
        if (!step.Switch)
        {
            store.SetPhase(target, step.Phase, atWork);
            return;
        }

        store.Select(target);
        _options.Log($"CodeAlta Node stops to start {target} on trial ({atWork} session(s) at work).");
        if (Interlocked.Exchange(ref _restartRequested, 1) == 0) _options.RequestRestart();
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested && !RestartRequested)
        {
            try
            {
                Tick(DateTimeOffset.UtcNow);
            }
            catch (UpdateException exception)
            {
                _options.Log($"The self-update could not read or write its state: {exception.Message}");
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
