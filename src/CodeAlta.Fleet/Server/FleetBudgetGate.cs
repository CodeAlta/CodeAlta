using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using XenoAtom.Logging;

namespace CodeAlta.Fleet.Server;

/// <summary>A server a machine joined, as its budgets see it: its anchored device list, the keys of the person's spaces, where its budgets are kept.</summary>
/// <param name="LinkId">The id of the link.</param>
/// <param name="Devices">The device list the machine anchored, whose key and HTTP handler the calls use too.</param>
/// <param name="Keys">The metadata keys of the person's spaces, which name the space and the project of a run as the server knows them; null names none.</param>
/// <param name="StatePath">The file the budgets of the link are kept in, so that a machine offline or restarted still holds its allowances; null keeps them in memory.</param>
public sealed record FleetBudgetLink(string LinkId, FleetOrganizationDevices Devices, FleetMetadataKeyring? Keys, string? StatePath)
{
    /// <summary>
    /// Gets the signed access document of the server, as the machine verified it: once it gives the machine to another person,
    /// the budgets of that person hold there, and those of the person the device list names no more. Null reads the device list alone.
    /// </summary>
    public FleetOrganizationAccess? Access { get; init; }
}

/// <summary>A run that a budget may limit: a session of the machine, in a space, of a project.</summary>
/// <param name="RunId">The run, which a Continue anyway is bound to.</param>
/// <param name="Title">What the run is, for the owner on this machine: a hold the server records names the run only, never its title.</param>
/// <param name="SpaceId">The space of the session on this machine; null for the default one.</param>
/// <param name="ProjectIdentity">What recognizes its project on the person's machines (<see cref="FleetProjectKeys.IdentityAsync"/>); null for none.</param>
public sealed record FleetBudgetRun(string RunId, string? Title, string? SpaceId, string? ProjectIdentity)
{
    /// <summary>Gets the nonce of the hold the run asks when a budget holds it; null for a new one. A run taken again after a restart asks the same hold.</summary>
    public string? Nonce { get; init; }

    /// <summary>
    /// Gets the person who started the session of the run on this machine through the organization (a team membership, a person
    /// the machine names), as the machine authenticated them; null for none. On a machine of a team or of the organization, the
    /// space and the project of the run are those of that person's keys.
    /// </summary>
    public string? PersonId { get; init; }

    /// <summary>Gets the organization of <see cref="PersonId"/>: a person of another organization is not one of the server's.</summary>
    public string? PersonOrganizationId { get; init; }
}

/// <summary>What this machine spent of a budget: the statistics facts it counted itself, which it holds offline too.</summary>
public interface IFleetBudgetSpend
{
    /// <summary>Sums what the sessions of this machine in the scope of a budget spent in a period.</summary>
    /// <param name="link">The server of the budget, whose keys name the space and the project of each session.</param>
    /// <param name="budget">The budget.</param>
    /// <param name="period">The period.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Tokens, or millionths of a US dollar.</returns>
    ValueTask<long> SpentAsync(FleetBudgetLink link, FleetBudgetDefinition budget, FleetBudgetPeriod period, CancellationToken cancellationToken);
}

/// <summary>Whether a run may start, as the budgets of its machine say.</summary>
/// <param name="Problem">Why it may not; null when it may.</param>
/// <param name="Continued">The budgets a person continued it anyway for.</param>
public sealed record FleetBudgetAdmission(string? Problem, IReadOnlyList<string> Continued)
{
    /// <summary>Gets an admission.</summary>
    public static FleetBudgetAdmission Admitted { get; } = new(null, []);
}

/// <summary>
/// What a machine checks before a run starts (<c>doc/fleet/observability.md</c>, budgets and alerts): the budgets of its owner
/// that a controller device of that owner signed and that cover the run (the machine, its owner, the space or the project of
/// its session). An enforced budget admits the run only while this machine holds enough of its allowance beyond what it spent
/// (counted from its own statistics, offline too); when it holds too little, it asks the server for more. An advisory budget
/// admits it unless the facts the server has, or this machine's own, reached the limit. A reached budget stops the run or holds
/// it until the owner continues it anyway: the machine verifies the signed Continue anyway against the device list it anchored,
/// so the server cannot release a run by itself.
/// </summary>
public sealed class FleetBudgetGate
{
    /// <summary>How long a held run waits for a person by default.</summary>
    public static readonly TimeSpan DefaultHoldWait = TimeSpan.FromHours(12);

    private readonly Func<IReadOnlyList<FleetBudgetLink>> _links;
    private readonly IFleetBudgetSpend _spend;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, LinkState> _states = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Initializes a new instance of the <see cref="FleetBudgetGate"/> class.</summary>
    /// <param name="links">Gives the servers the machine joined now.</param>
    /// <param name="spend">What this machine spent.</param>
    /// <param name="time">The clock; null for the system's.</param>
    /// <exception cref="ArgumentNullException"><paramref name="links"/> or <paramref name="spend"/> is null.</exception>
    public FleetBudgetGate(Func<IReadOnlyList<FleetBudgetLink>> links, IFleetBudgetSpend spend, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(spend);
        _links = links;
        _spend = spend;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Gets or sets how long a held run waits for a person.</summary>
    public TimeSpan HoldWait { get; set; } = DefaultHoldWait;

    /// <summary>Gets or sets how often a held run reads its hold again when the server says nothing.</summary>
    public TimeSpan HoldPoll { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets how long a read of the budgets may take before the ones kept are used.</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Raised when a run starts to wait for a person, with the budget and the run.</summary>
    public event Action<string, string>? Held;

    /// <summary>Wakes the runs that wait: a server said that budgets, allowances or holds changed.</summary>
    public void Changed() => Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>Admits a run, or tells why it may not start; it may wait for a person when a budget holds its runs.</summary>
    /// <param name="run">The run.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The admission.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    public Task<FleetBudgetAdmission> AdmitAsync(FleetBudgetRun run, CancellationToken cancellationToken) => AdmitAsync(run, null, cancellationToken);

    /// <summary>Admits a run, or tells why it may not start; it may wait for a person when a budget holds its runs, and says so first.</summary>
    /// <param name="run">The run.</param>
    /// <param name="held">What is told once the run starts to wait for a person; null for nothing.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The admission.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> is null.</exception>
    public async Task<FleetBudgetAdmission> AdmitAsync(FleetBudgetRun run, Action? held, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.RunId);
        var continued = new List<string>();
        foreach (var link in _links())
        {
            var state = _states.GetOrAdd(link.LinkId, _ => LinkState.Load(link.StatePath));
            await RefreshAsync(link, state, cancellationToken).ConfigureAwait(false);
            if (link.Devices.Current is not { } list)
            {
                // Budgets kept that this machine cannot verify now (its device list not read yet, the server away): the run waits for
                // them, never runs as if none held it.
                if (!state.Budgets().Any()) continue;
                if (await WaitForDevicesAsync(link, run, held, cancellationToken).ConfigureAwait(false) is not { } verified)
                    return new FleetBudgetAdmission("The budgets this machine keeps could not be verified: the run does not start.", continued);
                list = verified;
                held = null;
            }
            var access = link.Access?.Current;
            var scope = ScopeOf(link, list, access, run);
            foreach (var kept in state.Budgets())
            {
                // Kept once verified: a signer removed since takes no restriction away (FleetBudgetDocuments.TryVerifyKept).
                if (!FleetBudgetDocuments.TryVerifyKept(kept.Signed, list, access, link.Devices.Identity.DeviceId, out var budget, out _) || !FleetBudgetDocuments.Covers(budget, scope))
                    continue;
                if (await CheckAsync(link, state, kept, budget, cancellationToken).ConfigureAwait(false) is not { } period) continue;
                if (budget.Reached == FleetBudgetReachedActions.Stop)
                {
                    Log($"The run {run.RunId} does not start: the budget '{budget.Name}' is reached.");
                    return new FleetBudgetAdmission($"The budget '{budget.Name}' is reached: the run does not start.", continued);
                }

                var problem = await HoldAsync(link, state, budget, period, run, scope, held, cancellationToken).ConfigureAwait(false);
                if (problem is not null) return new FleetBudgetAdmission(problem, continued);
                continued.Add(budget.BudgetId);
            }
        }

        return continued.Count == 0 ? FleetBudgetAdmission.Admitted : new FleetBudgetAdmission(null, continued);
    }

    // Waits, as a run a budget holds, for the device list of a link that keeps budgets: null when it is not read within the wait.
    private async Task<FleetDeviceListState?> WaitForDevicesAsync(FleetBudgetLink link, FleetBudgetRun run, Action? held, CancellationToken cancellationToken)
    {
        Log($"The run {run.RunId} waits: the budgets this machine keeps cannot be verified yet.");
        held?.Invoke();
        var deadline = _time.GetUtcNow() + HoldWait;
        while (link.Devices.Current is null)
        {
            if (_time.GetUtcNow() >= deadline) return null;
            var changed = Volatile.Read(ref _changed).Task;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ReadTimeout);
                await link.Devices.RefreshAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or FleetServerException or FleetDeviceListException
                or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(HoldPoll);
                try { await changed.WaitAsync(wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
        }

        return link.Devices.Current;
    }

    // The period of a budget when it is reached for a new run of this machine; null when the run may start.
    private async Task<FleetBudgetPeriod?> CheckAsync(FleetBudgetLink link, LinkState state, KeptBudget kept, FleetBudgetDefinition budget, CancellationToken cancellationToken)
    {
        var period = FleetBudgetDocuments.PeriodOf(budget, _time.GetUtcNow());
        var spent = await _spend.SpentAsync(link, budget, period, cancellationToken).ConfigureAwait(false);
        if (budget.Mode == FleetBudgetModes.Advisory)
        {
            // The facts the server has of every machine, or this machine's own, when they are of this period.
            var serverReached = kept.PeriodStart == period.StartQuarter && kept.State == FleetBudgetStates.Reached;
            return serverReached || spent >= budget.Limit ? period : null;
        }

        // What the server gives is not signed: this machine never holds more than the signed limit, and its own spending that
        // reached the limit holds the run whatever the server says.
        if (spent >= budget.Limit) return period;
        var reserve = FleetBudgetDocuments.RunReserve(budget);
        var held = Math.Min(kept.PeriodStart == period.StartQuarter ? kept.Allowance : 0, budget.Limit);
        if (held - spent >= reserve) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReadTimeout);
            using var client = Client(link);
            var given = await client.AskAllowanceAsync(budget.BudgetId, new FleetAllowanceRequest(budget.Number, spent, reserve), timeout.Token).ConfigureAwait(false);
            var granted = Math.Clamp(given.Granted, 0, budget.Limit);
            state.SetAllowance(budget.BudgetId, given.PeriodStart, granted);
            Log($"The server gave this machine {granted} of the budget '{budget.Name}' for its period{(given.Exhausted ? "; the budget is reached" : string.Empty)}.");
            return granted - spent >= reserve ? null : period;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is FleetServerException or HttpRequestException or OperationCanceledException)
        {
            // Offline, a machine spends what it holds and no more: the run waits until it can ask.
            Log($"No allowance of the budget '{budget.Name}' could be asked: {exception.Message}");
            return period;
        }
    }

    // A run that waits for a person: the server records the hold, the owner continues it anyway (signed) or stops it.
    private async Task<string?> HoldAsync(FleetBudgetLink link, LinkState state, FleetBudgetDefinition budget, FleetBudgetPeriod period, FleetBudgetRun run,
        FleetBudgetRunScope scope, Action? held, CancellationToken cancellationToken)
    {
        var nonce = run.Nonce is { Length: > 0 and <= 128 } given && !given.Any(char.IsControl) ? given : Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var deadline = _time.GetUtcNow() + HoldWait;
        FleetBudgetHoldDto? hold = null;
        var announced = false;
        // The caller knows at once that the run waits, also while the server cannot be told yet.
        held?.Invoke();
        while (true)
        {
            var signal = Volatile.Read(ref _changed).Task;
            try
            {
                using var client = Client(link);
                hold = hold is null
                    ? await client.RequestBudgetHoldAsync(budget.BudgetId, new RequestFleetBudgetHoldRequest(run.RunId, nonce, budget.Number, period.StartQuarter, Title: null),
                        cancellationToken).ConfigureAwait(false)
                    : await client.GetBudgetHoldAsync(hold.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetServerException exception) when (exception.Code == "budget_hold.conflict")
            {
                return $"The run waits for the budget '{budget.Name}' with another hold already.";
            }
            catch (FleetServerException exception) when (exception.Code == "budget.not_found")
            {
                // A server that no longer knows the budget releases nothing: only a signed removal, or a version that is not reached,
                // lets the run start (read below), else the run waits as for a server that does not answer.
                hold = null;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is FleetServerException or HttpRequestException or OperationCanceledException)
            {
                // The server does not answer: the run keeps waiting, and asks again.
            }

            if (hold is not null && !announced)
            {
                announced = true;
                Log($"The run {run.RunId} waits for the budget '{budget.Name}': the owner may continue it anyway.");
                Held?.Invoke(budget.BudgetId, run.RunId);
            }

            switch (hold?.Status)
            {
                case FleetBudgetHoldStates.Stopped:
                    Log($"The run {run.RunId} was stopped at the budget '{budget.Name}'.");
                    return $"The budget '{budget.Name}' is reached and the run was stopped.";
                case FleetBudgetHoldStates.Continued when hold.Continue is { } signed:
                    // A Continue anyway is for the version of the budget and the period the run started to wait in: a newer version
                    // the machine verified since, or a new period, decides first, and one that still holds the run is not continued
                    // by a decision about another.
                    await RefreshAsync(link, state, cancellationToken).ConfigureAwait(false);
                    var again = await DecideAgainAsync(link, state, budget, run, scope, cancellationToken).ConfigureAwait(false);
                    if (again.Decided) return again.Problem;
                    if (again.Current is { } now && (now.Number != budget.Number || FleetBudgetDocuments.PeriodOf(now, _time.GetUtcNow()).StartQuarter != period.StartQuarter))
                    {
                        Log($"The run {run.RunId} does not start: it was continued anyway for an earlier version or period of the budget '{now.Name}', which still holds it.");
                        return $"The run was continued anyway for an earlier version or period of the budget '{now.Name}', which still holds it: start the run again to decide under the budget as it is now.";
                    }

                    if (await VerifyAsync(link, signed, budget, run, nonce, period, cancellationToken).ConfigureAwait(false) is { } refused)
                        return $"Continue anyway was refused: {refused}.";
                    Log($"The run {run.RunId} was continued anyway at the budget '{budget.Name}'.");
                    return null;
            }

            // Reached no more (a new period, a higher limit, an allowance given): the run starts.
            await RefreshAsync(link, state, cancellationToken).ConfigureAwait(false);
            var decided = await DecideAgainAsync(link, state, budget, run, scope, cancellationToken).ConfigureAwait(false);
            if (decided.Decided) return decided.Problem;

            var left = deadline - _time.GetUtcNow();
            if (left <= TimeSpan.Zero) return $"The budget '{budget.Name}' is reached and nobody continued the run in time.";
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(left < HoldPoll ? left : HoldPoll);
            try { await signal.WaitAsync(wait.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    // What the newest version of the budget of a held run that the machine verified decides, as for a new run, never by itself:
    // one that no longer covers the run, or that it is not reached under, starts it (decided, no problem); one that stops refuses
    // it (decided, with the problem); otherwise the budget still holds the run, under that version (Current). The machine keeps
    // every budget it verified: one it no longer has is one it cannot tell, and the run waits (not decided, no current version).
    private async Task<(bool Decided, string? Problem, FleetBudgetDefinition? Current)> DecideAgainAsync(FleetBudgetLink link, LinkState state, FleetBudgetDefinition budget,
        FleetBudgetRun run, FleetBudgetRunScope scope, CancellationToken cancellationToken)
    {
        var kept = state.Budgets().FirstOrDefault(candidate => candidate.Id == budget.BudgetId);
        if (kept is null || link.Devices.Current is not { } list
            || !FleetBudgetDocuments.TryVerifyKept(kept.Signed, list, link.Access?.Current, link.Devices.Identity.DeviceId, out var current, out _))
            return (false, null, null);
        if (current.Number != budget.Number && !FleetBudgetDocuments.Covers(current, scope)) return (true, null, current);
        if (await CheckAsync(link, state, kept, current, cancellationToken).ConfigureAwait(false) is null) return (true, null, current);
        if (current.Number != budget.Number && current.Reached == FleetBudgetReachedActions.Stop)
        {
            Log($"The run {run.RunId} does not start: the budget '{current.Name}' is reached and stops its runs now.");
            return (true, $"The budget '{current.Name}' is reached: the run does not start.", current);
        }

        return (false, null, current);
    }

    private async Task<string?> VerifyAsync(FleetBudgetLink link, FleetSignedBudgetContinue signed, FleetBudgetDefinition budget, FleetBudgetRun run, string nonce,
        FleetBudgetPeriod period, CancellationToken cancellationToken)
    {
        FleetDeviceListState list;
        try
        {
            list = await link.Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or FleetDeviceListException)
        {
            return $"the device list could not be read: {exception.Message}";
        }

        return FleetBudgetDocuments.TryVerifyContinue(signed, budget, list, link.Devices.Identity.DeviceId, run.RunId, nonce, period.StartQuarter, _time.GetUtcNow(), out _,
            out var reason) ? null : reason;
    }

    // The budgets of the server, read again before each run; those kept are used while the server does not answer.
    private async Task RefreshAsync(FleetBudgetLink link, LinkState state, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReadTimeout);
            if (link.Devices.Current is null) await link.Devices.RefreshAsync(timeout.Token).ConfigureAwait(false);
            using var client = Client(link);
            var listed = (await client.ListMachineBudgetsAsync(timeout.Token).ConfigureAwait(false)).Budgets;
            // What the server says is taken only as a newer version this machine verifies: what it leaves out, what does not
            // verify and an older version change nothing that the machine verified (a removal is a signed version too).
            if (link.Devices.Current is { } list)
                state.Merge(listed, signed => FleetBudgetDocuments.TryVerify(signed, list, link.Access?.Current, link.Devices.Identity.DeviceId, out var budget, out _) ? budget : null,
                    kept => FleetBudgetDocuments.TryVerifyKept(kept, list, link.Access?.Current, link.Devices.Identity.DeviceId, out var budget, out _) ? budget : null);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is FleetServerException or HttpRequestException or OperationCanceledException or FleetDeviceListException)
        {
            // Offline: the budgets and allowances kept hold.
        }
    }

    private static FleetBudgetRunScope ScopeOf(FleetBudgetLink link, FleetDeviceListState list, FleetAccessState? access, FleetBudgetRun run)
    {
        var machine = link.Devices.Identity.DeviceId;
        // The owner the signed access document names once the machine was given to another person, else the device list's; a
        // machine of a team or of the organization has none. Its space and its project are those of the keys that seal the
        // session: on a machine of a team or of the organization, those of the person who started it there, of this server's
        // organization; a run without one is covered by the budgets of the machine only.
        var shared = access?.IsSharedMachine(machine) == true;
        var owner = shared ? null : FleetAccessState.OwnerOfMachine(access, machine, list);
        var person = run.PersonOrganizationId is { } of && of == list.OrganizationId ? run.PersonId : null;
        var key = link.Keys?.CurrentFor(run.SpaceId, person);
        var project = key is not null && run.ProjectIdentity is { Length: > 0 } identity ? FleetProjectKeys.Of(key, identity) : null;
        return new FleetBudgetRunScope(machine, owner, key?.Grant?.SpaceId, project);
    }

    private static FleetServerClient Client(FleetBudgetLink link)
        => link.Devices.CreateClient();

    private static void Log(string message)
    {
        if (LogManager.IsInitialized) LogManager.GetLogger("CodeAlta.Fleet.Budgets").Info(message);
    }

    // The budgets of one server as this machine last read them, with what it holds of each; kept in a file of the link.
    private sealed class LinkState
    {
        private readonly string? _path;
        private readonly Lock _gate = new();
        private Dictionary<string, KeptBudget> _budgets;

        private LinkState(string? path, Dictionary<string, KeptBudget> budgets)
        {
            _path = path;
            _budgets = budgets;
        }

        public static LinkState Load(string? path)
        {
            var budgets = new Dictionary<string, KeptBudget>(StringComparer.Ordinal);
            if (path is not null)
            {
                try
                {
                    foreach (var kept in JsonSerializer.Deserialize(File.ReadAllBytes(path), FleetBudgetGateJsonContext.Default.ListKeptBudget) ?? [])
                        if (kept.Id is { Length: > 0 } && kept.Signed is not null) budgets[kept.Id] = kept;
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException or IOException or UnauthorizedAccessException)
                {
                    // Nothing kept: the budgets are read from the server.
                }
            }

            return new LinkState(path, budgets);
        }

        public IReadOnlyList<KeptBudget> Budgets()
        {
            lock (_gate) return [.. _budgets.Values];
        }

        /// <summary>
        /// Takes what the server says of the budgets: a version it verifies, for the budget it names, newer than the one kept
        /// (or the same, whose allowance and state the server updates). A budget the server leaves out, a version that does
        /// not verify, and an older one keep what is kept: only a newer signed version, a removal included, changes a budget.
        /// </summary>
        public void Merge(IReadOnlyList<FleetMachineBudgetDto> budgets, Func<FleetSignedBudget?, FleetBudgetDefinition?> verify,
            Func<FleetSignedBudget?, FleetBudgetDefinition?> verifyKept)
        {
            lock (_gate)
            {
                foreach (var budget in budgets)
                {
                    if (verify(budget.Signed) is not { } incoming || incoming.BudgetId != budget.Id) continue;
                    var known = _budgets.GetValueOrDefault(budget.Id);
                    // The version kept was verified when it was kept: its signer removed since, an older version still does not replace it.
                    var knownNumber = known is not null && verifyKept(known.Signed) is { } held ? held.Number : (long?)null;
                    if (knownNumber > incoming.Number) continue;
                    // The allowance the server says, or more when this machine was given more since (a read raced a grant).
                    var period = FleetStatisticsFamilies.QuarterOf(budget.Usage.PeriodStart);
                    var allowance = known is not null && known.PeriodStart == period ? Math.Max(known.Allowance, budget.Allowance) : budget.Allowance;
                    _budgets[budget.Id] = new KeptBudget { Id = budget.Id, Signed = budget.Signed, PeriodStart = period, Allowance = allowance, State = budget.Usage.State };
                }

                Save();
            }
        }

        public void SetAllowance(string budgetId, long periodStart, long granted)
        {
            lock (_gate)
            {
                if (!_budgets.TryGetValue(budgetId, out var kept)) return;
                _budgets[budgetId] = kept with
                {
                    PeriodStart = periodStart,
                    Allowance = kept.PeriodStart == periodStart ? Math.Max(kept.Allowance, granted) : granted,
                    State = kept.PeriodStart == periodStart ? kept.State : FleetBudgetStates.Ok,
                };
                Save();
            }
        }

        private void Save()
        {
            if (_path is null) return;
            try
            {
                FleetFiles.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes([.. _budgets.Values], FleetBudgetGateJsonContext.Default.ListKeptBudget));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log($"The budgets of the machine could not be kept: {exception.Message}");
            }
        }
    }
}

/// <summary>A budget a machine keeps: as signed, with the period it last knew of it, what it holds of it and where the server said it stood.</summary>
internal sealed record KeptBudget
{
    public string? Id { get; init; }

    public FleetSignedBudget? Signed { get; init; }

    public long PeriodStart { get; init; }

    public long Allowance { get; init; }

    public string? State { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<KeptBudget>))]
internal sealed partial class FleetBudgetGateJsonContext : JsonSerializerContext;

/// <summary>
/// What this machine spent of a budget, from the statistics facts it counts itself (<see cref="IFleetStatisticsSource"/>): the
/// sessions of the scope (all of them for the machine or its owner; those of the space or the project, by the keys of the server),
/// their rows of the period. Private sessions count too: they spend as well, though they publish nothing.
/// </summary>
public sealed class FleetBudgetLocalSpend : IFleetBudgetSpend
{
    private readonly IFleetPublicationSource _sessions;
    private readonly IFleetStatisticsSource _statistics;
    private readonly ConcurrentDictionary<string, (string Revision, long From, IReadOnlyList<FleetStatisticsRow> Rows)> _rows = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="FleetBudgetLocalSpend"/> class.</summary>
    /// <param name="sessions">The sessions of the machine, with their space and project.</param>
    /// <param name="statistics">The statistics facts of the machine.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FleetBudgetLocalSpend(IFleetPublicationSource sessions, IFleetStatisticsSource statistics)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(statistics);
        _sessions = sessions;
        _statistics = statistics;
    }

    /// <inheritdoc />
    public async ValueTask<long> SpentAsync(FleetBudgetLink link, FleetBudgetDefinition budget, FleetBudgetPeriod period, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(period);
        var revisions = await _statistics.ReadRevisionsAsync(cancellationToken).ConfigureAwait(false);
        if (revisions.Count == 0) return 0;
        var sessions = (await _sessions.ReadSessionsAsync(cancellationToken).ConfigureAwait(false))
            .Concat(await _sessions.ReadRetainedSessionsAsync(cancellationToken).ConfigureAwait(false))
            .DistinctBy(static session => session.SessionId, StringComparer.Ordinal);
        long spent = 0;
        foreach (var session in sessions)
        {
            if (!revisions.TryGetValue(session.SessionId, out var revision) || !InScope(link, budget, session)) continue;
            if (!_rows.TryGetValue(session.SessionId, out var cached) || cached.Revision != revision || cached.From > period.StartQuarter)
            {
                cached = (revision, period.StartQuarter, await _statistics.ReadRowsAsync(session.SessionId, period.StartQuarter, cancellationToken).ConfigureAwait(false));
                _rows[session.SessionId] = cached;
            }

            foreach (var row in cached.Rows)
            {
                if (row.Quarter >= period.StartQuarter && row.Quarter < period.EndQuarter) spent += FleetBudgetDocuments.Count(budget.Measure, row);
            }
        }

        return spent;
    }

    private static bool InScope(FleetBudgetLink link, FleetBudgetDefinition budget, FleetLocalSession session)
    {
        switch (budget.ScopeKind)
        {
            case FleetBudgetScopeKinds.Person:
            case FleetBudgetScopeKinds.Machine:
                return true;
            case FleetBudgetScopeKinds.Space:
                return KeyOf(link, session)?.Grant?.SpaceId is { } space && space == budget.ScopeId;
            case FleetBudgetScopeKinds.Project:
                return KeyOf(link, session) is { } key && session.ProjectIdentity is { Length: > 0 } identity
                    && FleetProjectKeys.Of(key, identity) == budget.ScopeId;
            default:
                return false;
        }
    }

    // The key of the space of a session, as it seals it: on a machine of a team or of the organization, the one of its person, of
    // this server's organization.
    private static FleetMetadataKey? KeyOf(FleetBudgetLink link, FleetLocalSession session)
        => link.Keys?.CurrentFor(session.SpaceId, session.PersonOrganizationId is { } of && of == link.Devices.Current?.OrganizationId ? session.PersonId : null);
}
