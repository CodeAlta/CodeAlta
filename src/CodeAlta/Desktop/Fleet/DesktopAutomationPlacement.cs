using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Automations;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Fleet;

/// <summary>Where an automation placed on a paired machine stands there.</summary>
/// <param name="MachineId">The machine it runs on.</param>
/// <param name="MachineName">Its name; null when no paired machine has that id.</param>
/// <param name="State"><c>copied</c> (the machine has it as it is), <c>pending</c> (it is copied once the machine answers) or <c>failed</c> (the machine refused it).</param>
/// <param name="Message">Why it is pending or failed; null when copied.</param>
/// <param name="CopiedAt">When the machine last took it.</param>
internal sealed record AutomationPlacementStatus(string MachineId, string? MachineName, string State, string? Message, DateTimeOffset? CopiedAt);

/// <summary>
/// Places the automations of the user that run on a paired machine (<see cref="AutomationDefinition.RunsOn"/>): copies each
/// one there through the node API (<c>automations.put</c>) whenever it changes, removes it from a machine it left, runs it
/// there by hand, and keeps the latest runs the machine reports. The definition stays in the configuration of the user on
/// this computer; the machine runs its copy by its triggers, while this computer is closed.
/// </summary>
/// <remarks>
/// One loop does the work, woken by a change of the automations or of the machines, and every half minute: it is the
/// only writer of the record of what was copied where (<c>fleet/placed-automations.json</c>), so that a change made
/// while a machine is offline reaches it when it answers.
/// </remarks>
internal sealed class DesktopAutomationPlacement : IAutomationPlacement, IAsyncDisposable
{
    /// <summary>The name of the record of what was copied where, in the fleet folder of the state root.</summary>
    public const string FileName = "placed-automations.json";

    /// <summary>The name of the record of the last version number signed per automation, kept when an automation leaves its machine.</summary>
    public const string NumbersFileName = "automation-version-numbers.json";

    private const int RunsKept = 50;
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RunsReadEvery = TimeSpan.FromSeconds(15);

    private readonly AutomationService _automations;
    private readonly Func<string, RemoteSessionHost?> _machines;
    private readonly string _path;
    private readonly Func<string, CancellationToken, Task<string?>> _repositoryOf;
    private readonly Func<string> _deviceId;
    private readonly Func<FleetAutomationVersion, FleetSignedAutomationVersion> _sign;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PlacedAutomation> _placed;
    private readonly Dictionary<string, AutomationPlacementStatus> _status = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<AutomationRun>> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _runsReadAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _numbers;
    private readonly string _numbersPath;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Logger _logger = LogManager.GetLogger("CodeAlta.Desktop.Automations");
    private Task _loop = Task.CompletedTask;

    /// <summary>Creates the placement; nothing is copied before <see cref="Start"/>.</summary>
    /// <param name="automations">The automations of the user.</param>
    /// <param name="machines">Finds the host of a paired machine by its id.</param>
    /// <param name="fleetRoot">The fleet folder of the state root, where the record is kept.</param>
    /// <param name="deviceId">Gives the device id of this computer, as the machines know it.</param>
    /// <param name="sign">Signs a version of an automation with the key of this computer, which the machine checks before it runs it.</param>
    /// <param name="repositoryOf">Reads the repository key of a project folder; null for its git remote.</param>
    internal DesktopAutomationPlacement(AutomationService automations, Func<string, RemoteSessionHost?> machines, string fleetRoot, Func<string> deviceId,
        Func<FleetAutomationVersion, FleetSignedAutomationVersion> sign, Func<string, CancellationToken, Task<string?>>? repositoryOf = null)
    {
        ArgumentNullException.ThrowIfNull(automations);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentException.ThrowIfNullOrWhiteSpace(fleetRoot);
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(sign);
        (_automations, _machines, _deviceId, _sign) = (automations, machines, deviceId, sign);
        _path = Path.Combine(fleetRoot, FileName);
        _numbersPath = Path.Combine(fleetRoot, NumbersFileName);
        _numbers = LoadNumbers(_numbersPath);
        _repositoryOf = repositoryOf ?? RepositoryKey.ReadAsync;
        _placed = Load(_path);
    }

    /// <summary>Raised, on any thread, when where an automation stands on its machine, or its runs there, changed.</summary>
    internal event Action? Changed;

    /// <summary>
    /// Gets what finds the way to a machine this computer is not paired with, through a server that lists it; null for
    /// none. The automation is then given to the server, signed and sealed for the machine, which takes it when it connects.
    /// </summary>
    internal Func<string, DesktopServerAutomationRoute?>? ServerRoute { get; init; }

    /// <summary>Gets what signs a run asked by hand of a machine reached through a server; null runs none by hand there.</summary>
    internal Func<FleetAutomationRunRequest, FleetSignedAutomationRunRequest>? SignRun { get; init; }

    /// <summary>Gets how often the runs of a placed automation, and where it stands, are read again from its machine or server; tests shorten it.</summary>
    internal TimeSpan RunsReadInterval { get; init; } = RunsReadEvery;

    /// <summary>Starts copying, and keeps the automations and their runs up to date.</summary>
    internal void Start()
    {
        _automations.Changed += Poke;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Asks the loop to look at the automations and the machines now.</summary>
    internal void Poke()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* A look is asked already. */ }
        catch (ObjectDisposedException) { /* Closing. */ }
    }

    /// <summary>Where an automation placed on a machine stands there; null for one that runs on this computer.</summary>
    internal AutomationPlacementStatus? Status(AutomationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Definition.RunsOn is not { } machine) return null;
        lock (_gate)
        {
            if (_status.TryGetValue(entry.Id, out var status) && status.MachineId == machine) return status;
        }

        return new(machine, _machines(machine)?.Machine.Name ?? Route(machine, static route => route.MachineName), "pending", "It is copied to the machine in a moment.", null);
    }

    /// <summary>The latest runs the machine of an automation reported, newest first.</summary>
    internal IReadOnlyList<AutomationRun> Runs(string id)
    {
        lock (_gate) return _runs.GetValueOrDefault(id) ?? [];
    }

    /// <inheritdoc />
    public async Task<AutomationRun> RunAsync(AutomationEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var now = DateTimeOffset.UtcNow;
        AutomationRun Failed(string message) => new(Guid.CreateVersion7().ToString("N"), entry.Id, now, "manual")
            { Name = entry.Definition.Name, Status = AutomationRun.Failed, Message = message, EndedAt = now };
        if (entry.Definition.RunsOn is not { } machineId) return Failed("It runs on this computer.");
        if (_machines(machineId) is not { IsPaired: true } && ServerRoute?.Invoke(machineId) is { } route)
        {
            using (route) return await RunThroughServerAsync(entry, machineId, route, Failed, cancellationToken).ConfigureAwait(false);
        }

        if (_machines(machineId) is not { } host) return Failed("No paired machine has the id " + machineId + ".");
        if (host.CurrentClient is not { } client) return Failed($"The machine {host.Machine.Name} is {RemoteSessionHost.Describe(host.State)}: it cannot run it now.");
        try
        {
            // The machine runs the automation as it is here: a change not copied yet goes first.
            await _work.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CopyAsync(entry, machineId, host.Machine.Name, client, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _work.Release();
            }

            if (Status(entry) is { State: not "copied" } status) return Failed(status.Message ?? "It could not be copied to the machine.");
            var run = await client.RunAutomationAsync(entry.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
            lock (_gate) _runsReadAt.Remove(entry.Id);
            Poke();
            return ToRun(run);
        }
        catch (Exception exception) when (exception is FleetException or IOException or ObjectDisposedException or FleetConnectionClosedException)
        {
            return Failed($"{host.Machine.Name}: {exception.Message}");
        }
    }

    /// <summary>Stops the loop; what was copied stays on the machines.</summary>
    public async ValueTask DisposeAsync()
    {
        _automations.Changed -= Poke;
        if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    /// <summary>Looks once at the automations and the machines: copies, removes, and reads the runs. Tests call it directly.</summary>
    internal async Task SyncAsync(CancellationToken cancellationToken)
    {
        await _work.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = _automations.Snapshot;
            if (!snapshot.Scanned) return;
            var placed = snapshot.Entries.Where(static entry => entry.Definition.RunsOn is not null).ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
            var changed = false;

            // What left a machine (deleted, placed elsewhere or back here) is removed there once it answers.
            KeyValuePair<string, PlacedAutomation>[] records;
            lock (_gate) records = [.. _placed];
            foreach (var (id, record) in records)
            {
                if (placed.TryGetValue(id, out var entry) && entry.Definition.RunsOn == record.MachineId && (record.Via is null || !IsPaired(record.MachineId))) continue;
                if (record.Via is not null)
                {
                    changed |= await RemoveThroughServerAsync(id, record, placed.ContainsKey(id), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (_machines(record.MachineId) is not { CurrentClient: { } client }) continue;
                try
                {
                    await client.RemoveAutomationAsync(id, cancellationToken: cancellationToken).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _placed.Remove(id);
                        if (!placed.ContainsKey(id)) { _status.Remove(id); _runs.Remove(id); }
                        Save();
                    }

                    changed = true;
                }
                catch (Exception exception) when (exception is FleetException or IOException or ObjectDisposedException or FleetConnectionClosedException)
                {
                    Log($"The automation {id} could not be removed from the machine {record.MachineId}: {exception.Message}");
                }
            }

            foreach (var entry in placed.Values)
            {
                var machineId = entry.Definition.RunsOn!;
                var host = _machines(machineId);
                if (host is not { IsPaired: true } && ServerRoute?.Invoke(machineId) is { } route)
                {
                    using (route)
                    {
                        changed |= await CopyThroughServerAsync(entry, machineId, route, cancellationToken).ConfigureAwait(false);
                        changed |= await ReadThroughServerAsync(entry.Id, machineId, route, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (host?.CurrentClient is not { } client)
                {
                    changed |= SetStatus(entry.Id, PendingStatus(entry, machineId, host));
                    continue;
                }

                changed |= await CopyAsync(entry, machineId, host.Machine.Name, client, cancellationToken).ConfigureAwait(false);
                changed |= await ReadRunsAsync(entry.Id, client, cancellationToken).ConfigureAwait(false);
            }

            if (changed) Changed?.Invoke();
        }
        finally
        {
            _work.Release();
        }
    }

    private async Task LoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One look that failed is one look: the next one tries again.
                Log("The automations placed on other machines could not be looked at: " + exception.Message);
            }

            try
            {
                await _signal.WaitAsync(RefreshEvery, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // Under _work. Copies an automation to its machine unless the machine has it as it is now. Returns whether its state changed.
    private async Task<bool> CopyAsync(AutomationEntry entry, string machineId, string machineName, FleetNodeClient client, CancellationToken cancellationToken)
    {
        if (entry.Problem is { } problem) return SetStatus(entry.Id, new(machineId, machineName, "failed", problem, null));
        string? repository = null;
        if (entry.ProjectPath is { } folder)
        {
            repository = await _repositoryOf(folder, cancellationToken).ConfigureAwait(false);
            if (repository is null)
                return SetStatus(entry.Id, new(machineId, machineName, "failed", "Its project has no remote repository that the other machine could find.", null));
        }

        // What the machine runs: the definition without where it runs here, its project being found there by its repository.
        var copy = entry.Definition with { RunsOn = null, Project = null };
        var key = Key(copy, repository);
        lock (_gate)
        {
            if (_placed.TryGetValue(entry.Id, out var record) && record.MachineId == machineId && record.Key == key && record.Via is null)
                return SetStatus(entry.Id, new(machineId, machineName, "copied", null, record.CopiedAt));
        }

        try
        {
            // The machine runs what this computer signed for it, numbered after every version it signed of this automation.
            var definition = AutomationConfig.Write(string.Empty, copy);
            var number = NextNumber(entry.Id);
            var signed = _sign(new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, null, entry.Id, number, machineId, false, copy.Enabled, TriggerKinds(copy),
                definition, repository, null, _deviceId(), string.Empty, DateTimeOffset.UtcNow));
            // A copy is the same whatever the number of times it is made: each attempt has a key of its own, so that one
            // made again after a removal is made, not answered from the receipt of the first.
            await client.PutAutomationAsync(new FleetAutomationPutParams(definition, repository) { Signed = signed }, cancellationToken: cancellationToken).ConfigureAwait(false);
            lock (_gate) _numbers[entry.Id] = number;
        }
        catch (ArgumentException exception)
        {
            return SetStatus(entry.Id, new(machineId, machineName, "failed", exception.Message, null));
        }
        catch (FleetException exception)
        {
            return SetStatus(entry.Id, new(machineId, machineName, "failed", exception.Error.Code == FleetErrorCodes.Forbidden
                ? $"{machineName} does not let this computer give it automations: on that machine, run altanode permissions {_deviceId()} --allow automate."
                : exception.Message, null));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or FleetConnectionClosedException)
        {
            return SetStatus(entry.Id, new(machineId, machineName, "pending", "The machine did not answer: it is copied when it does.", null));
        }

        var copiedAt = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _placed[entry.Id] = new PlacedAutomation(machineId, key, copiedAt) { Number = _numbers.GetValueOrDefault(entry.Id) };
            Save();
        }

        Log($"The automation '{entry.Definition.Name}' ({entry.Id}) was copied to {machineName}.");
        SetStatus(entry.Id, new(machineId, machineName, "copied", null, copiedAt));
        return true;
    }

    private async Task<bool> ReadRunsAsync(string id, FleetNodeClient client, CancellationToken cancellationToken)
    {
        // A machine's list of sessions changes every few seconds: its runs are read again at most every so often, or at once after a run by hand.
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_runsReadAt.TryGetValue(id, out var readAt) && now - readAt < RunsReadInterval && now >= readAt) return false;
            _runsReadAt[id] = now;
        }

        IReadOnlyList<AutomationRun> runs;
        try
        {
            runs = [.. (await client.ListAutomationRunsAsync(id, RunsKept, cancellationToken).ConfigureAwait(false)).Runs.Select(ToRun)];
        }
        catch (Exception exception) when (exception is FleetException or IOException or ObjectDisposedException or FleetConnectionClosedException)
        {
            // Not read: the next look tries again.
            lock (_gate) _runsReadAt.Remove(id);
            return false;
        }

        lock (_gate)
        {
            if (_runs.TryGetValue(id, out var known) && known.SequenceEqual(runs)) return false;
            _runs[id] = runs;
            return true;
        }
    }

    // Asks the machine, through its server, to run the automation now, with a request this computer signs; the run is the
    // machine's, read with the others. A change not given yet goes first.
    private async Task<AutomationRun> RunThroughServerAsync(AutomationEntry entry, string machineId, DesktopServerAutomationRoute route, Func<string, AutomationRun> failed,
        CancellationToken cancellationToken)
    {
        if (SignRun is not { } sign) return failed($"{route.MachineName} is reached through a server: it runs this automation by its triggers.");
        await _work.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CopyThroughServerAsync(entry, machineId, route, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _work.Release();
        }

        if (Status(entry) is { State: "failed" } status) return failed(status.Message ?? "It could not be given to the machine.");
        var runId = Guid.CreateVersion7().ToString("N");
        var now = DateTimeOffset.UtcNow;
        try
        {
            var signed = sign(new FleetAutomationRunRequest(FleetAutomationRunRequest.CurrentVersion, route.OrganizationId, entry.Id, machineId, runId, string.Empty,
                string.Empty, now));
            await route.RunAsync(signed, cancellationToken).ConfigureAwait(false);
        }
        catch (FleetServerException exception)
        {
            return failed($"{route.MachineName}: {exception.Message}");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or ArgumentException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return failed($"The server of {route.MachineName} did not take the request: {exception.Message}");
        }

        lock (_gate) _runsReadAt.Remove(entry.Id);
        Poke();
        return new AutomationRun(runId, entry.Id, now, "manual") { Name = entry.Definition.Name, Status = AutomationRun.Running };
    }

    // Under _work. Gives an automation to the server of a machine this computer is not paired with, signed and sealed for
    // that machine, unless the server holds it as it is now. Returns whether its state changed.
    private async Task<bool> CopyThroughServerAsync(AutomationEntry entry, string machineId, DesktopServerAutomationRoute route, CancellationToken cancellationToken)
    {
        var machineName = route.MachineName;
        if (entry.Problem is { } problem) return SetStatus(entry.Id, new(machineId, machineName, "failed", problem, null));
        string? repository = null;
        if (entry.ProjectPath is { } folder)
        {
            repository = await _repositoryOf(folder, cancellationToken).ConfigureAwait(false);
            if (repository is null)
                return SetStatus(entry.Id, new(machineId, machineName, "failed", "Its project has no remote repository that the other machine could find.", null));
        }

        var copy = entry.Definition with { RunsOn = null, Project = null };
        var key = Key(copy, repository);
        lock (_gate)
        {
            // Given already: where it stands is what the server last said (read with the runs).
            if (_placed.TryGetValue(entry.Id, out var record) && record.MachineId == machineId && record.Key == key && record.Via == route.LinkId)
                return _status.TryGetValue(entry.Id, out var known) && known.MachineId == machineId
                    ? false
                    : SetStatus(entry.Id, new(machineId, machineName, "pending", $"Given to the server: {machineName} takes it when it connects.", null));
        }

        var number = NextNumber(entry.Id);
        try
        {
            // The server reads only the facts beside it: what it does is sealed for the machine and this person's controllers.
            var sealedText = await route.SealAsync(new FleetAutomationContent(AutomationConfig.Write(string.Empty, copy), repository), entry.Id, number, machineId,
                cancellationToken).ConfigureAwait(false);
            var signed = _sign(new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, route.OrganizationId, entry.Id, number, machineId, false, copy.Enabled,
                TriggerKinds(copy), null, null, sealedText, _deviceId(), string.Empty, DateTimeOffset.UtcNow));
            await route.PutAsync(signed, cancellationToken).ConfigureAwait(false);
            lock (_gate) _numbers[entry.Id] = number;
        }
        catch (ArgumentException exception)
        {
            return SetStatus(entry.Id, new(machineId, machineName, "failed", exception.Message, null));
        }
        catch (FleetServerException exception) when (exception.Status is System.Net.HttpStatusCode.Forbidden)
        {
            return SetStatus(entry.Id, new(machineId, machineName, "failed", $"{machineName} is not one of your machines: you give automations to the machines you own.", null));
        }
        catch (FleetServerException exception) when ((int)exception.Status is >= 400 and < 500)
        {
            return SetStatus(entry.Id, new(machineId, machineName, "failed", exception.Message, null));
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or FleetDeviceListException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return SetStatus(entry.Id, new(machineId, machineName, "pending", "The server did not answer: it is given when it does.", null));
        }

        lock (_gate)
        {
            _placed[entry.Id] = new PlacedAutomation(machineId, key, DateTimeOffset.UtcNow) { Number = number, Via = route.LinkId };
            _runsReadAt.Remove(entry.Id);
            Save();
        }

        Log($"The automation '{entry.Definition.Name}' ({entry.Id}) was given to {machineName} through its server.");
        SetStatus(entry.Id, new(machineId, machineName, "pending", $"Given to the server: {machineName} takes it when it connects.", null));
        return true;
    }

    // Under _work. Reads where an automation given through a server stands (taken, waiting, refused by the machine) and its runs, at most every so often.
    private async Task<bool> ReadThroughServerAsync(string id, string machineId, DesktopServerAutomationRoute route, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        PlacedAutomation? record;
        lock (_gate)
        {
            if (!_placed.TryGetValue(id, out record) || record.Via != route.LinkId || record.MachineId != machineId) return false;
            if (_runsReadAt.TryGetValue(id, out var readAt) && now - readAt < RunsReadInterval && now >= readAt) return false;
            _runsReadAt[id] = now;
        }

        AutomationDto? held;
        IReadOnlyList<AutomationRun> runs;
        try
        {
            held = (await route.ListAsync(cancellationToken).ConfigureAwait(false)).Automations.FirstOrDefault(item => item.Id == id && item.MachineId == machineId);
            runs = [.. (await route.ListRunsAsync(id, RunsKept, cancellationToken).ConfigureAwait(false)).Runs.Select(run => new AutomationRun(run.RunId, run.AutomationId, run.StartedAt,
                run.Trigger)
            {
                Name = _automations.Snapshot.Find(id)?.Definition.Name, SessionId = run.SessionId, Status = run.Status == "claimed" ? AutomationRun.Running : run.Status,
                Message = run.Message, EndedAt = run.EndedAt, InputTokens = run.InputTokens, OutputTokens = run.OutputTokens, Cost = run.Cost, CostUnit = run.CostUnit,
            })];
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            lock (_gate) _runsReadAt.Remove(id);
            return false;
        }

        var name = route.MachineName;
        var status = held switch
        {
            { Number: var number, State: "applied" } when number == record.Number => new AutomationPlacementStatus(machineId, name, "copied", null, record.CopiedAt),
            { Number: var number, State: "refused" } when number == record.Number => new AutomationPlacementStatus(machineId, name, "failed",
                $"{name} refused it: {held.Message ?? "no reason given"}.", null),
            null => new AutomationPlacementStatus(machineId, name, "pending", "The server no longer holds it: it is given again.", null),
            _ => new AutomationPlacementStatus(machineId, name, "pending", $"Given to the server: {name} takes it when it connects.", null),
        };
        if (held is null)
        {
            lock (_gate)
            {
                _placed.Remove(id);
                Save();
            }
        }

        var changed = SetStatus(id, status);
        lock (_gate)
        {
            if (_runs.TryGetValue(id, out var known) && known.SequenceEqual(runs)) return changed;
            _runs[id] = runs;
            return true;
        }
    }

    // Under _work. Takes an automation away from a machine it was given through a server: a signed removal, newer than the version given.
    private async Task<bool> RemoveThroughServerAsync(string id, PlacedAutomation record, bool stillPlaced, CancellationToken cancellationToken)
    {
        if (ServerRoute?.Invoke(record.MachineId) is not { } route) return false;
        using (route)
        {
            var number = NextNumber(id);
            try
            {
                var removal = _sign(new FleetAutomationVersion(FleetAutomationVersion.CurrentVersion, route.OrganizationId, id, number, record.MachineId, true, false, [], null, null,
                    null, _deviceId(), string.Empty, DateTimeOffset.UtcNow));
                await route.PutAsync(removal, cancellationToken).ConfigureAwait(false);
            }
            catch (FleetServerException exception) when (exception.Status is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.Forbidden)
            {
                // The server holds a newer version, or none: nothing of this computer is left to take away there.
                Log($"The automation {id} was not taken away from {route.MachineName} through its server: {exception.Message}");
            }
            catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or ArgumentException or TaskCanceledException
                && !cancellationToken.IsCancellationRequested)
            {
                Log($"The automation {id} could not be taken away from {route.MachineName} through its server: {exception.Message}");
                return false;
            }

            lock (_gate)
            {
                _numbers[id] = number;
                _placed.Remove(id);
                if (!stillPlaced) { _status.Remove(id); _runs.Remove(id); }
                Save();
            }

            return true;
        }
    }

    private bool IsPaired(string machineId) => _machines(machineId) is { IsPaired: true };

    private T? Route<T>(string machineId, Func<DesktopServerAutomationRoute, T> read) where T : class
    {
        if (ServerRoute?.Invoke(machineId) is not { } route) return null;
        using (route) return read(route);
    }

    private AutomationPlacementStatus PendingStatus(AutomationEntry entry, string machineId, RemoteSessionHost? host)
    {
        if (host is null) return new(machineId, null, "failed", "No machine this computer reaches has this id: choose another machine, or This computer.", null);
        lock (_gate)
        {
            var copiedAt = _placed.TryGetValue(entry.Id, out var record) && record.MachineId == machineId ? record.CopiedAt : (DateTimeOffset?)null;
            return new(machineId, host.Machine.Name, copiedAt is null ? "pending" : "copied",
                $"{host.Machine.Name} is {RemoteSessionHost.Describe(host.State)}" + (copiedAt is null ? ": it is copied when the machine answers." : ": it runs there what was last copied."),
                copiedAt);
        }
    }

    private bool SetStatus(string id, AutomationPlacementStatus status)
    {
        lock (_gate)
        {
            if (_status.TryGetValue(id, out var known) && known == status) return false;
            _status[id] = status;
            return true;
        }
    }

    private static AutomationRun ToRun(FleetAutomationRunInfo run)
        => new(run.Id, run.AutomationId, run.StartedAt, run.Trigger)
        {
            Name = run.Name, SessionId = run.SessionId, Detail = run.Detail, Status = run.Status, Message = run.Message, EndedAt = run.EndedAt,
            InputTokens = run.InputTokens, OutputTokens = run.OutputTokens, Cost = run.Cost, CostUnit = run.CostUnit,
        };

    // What the machine was given: the definition, whether it is enabled, and the repository of its project, as a signed version.
    private static string Key(AutomationDefinition copy, string? repository)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            "signed\n" + copy.Fingerprint() + "\n" + (copy.Enabled ? "1" : "0") + "\n" + repository)))[..32];

    /// <summary>The kinds of the triggers of an automation, the facts a version says in clear.</summary>
    internal static IReadOnlyList<string> TriggerKinds(AutomationDefinition definition)
        => [.. definition.Triggers.Select(static trigger => trigger.Kind.ToString().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Take(FleetAutomationVersionDocuments.MaxTriggers)];

    /// <summary>Gets the clock the version numbers start from; tests set one.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Reserves the number of the next version of an automation: after every number this computer signed for it (kept in a
    /// file of its own, also once the automation left its machine), and at least the time in milliseconds, so that a record
    /// lost here still numbers after what a machine holds. A clock set back never gives a number below the last one.
    /// </summary>
    internal long NextNumber(string id)
    {
        lock (_gate)
        {
            var held = Math.Max(_numbers.GetValueOrDefault(id), _placed.TryGetValue(id, out var record) ? record.Number : 0);
            var number = Math.Max(held + 1, Time.GetUtcNow().ToUnixTimeMilliseconds());
            _numbers[id] = number;
            SaveNumbers();
            return number;
        }
    }

    private static Dictionary<string, long> LoadNumbers(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = JsonSerializer.Deserialize(stream, PlacedAutomationJsonContext.Default.DictionaryStringInt64);
            return new((read ?? []).Where(static pair => pair.Value > 0), StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Without it, the clock and the placement record still number after what the machines hold.
            return new(StringComparer.Ordinal);
        }
    }

    // Under the gate.
    private void SaveNumbers()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_numbersPath)!);
            var staged = _numbersPath + ".tmp";
            File.WriteAllText(staged, JsonSerializer.Serialize(_numbers, PlacedAutomationJsonContext.Default.DictionaryStringInt64));
            File.Move(staged, _numbersPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log("The version numbers of the placed automations could not be written: " + exception.Message);
        }
    }

    private static Dictionary<string, PlacedAutomation> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = JsonSerializer.Deserialize(stream, PlacedAutomationJsonContext.Default.DictionaryStringPlacedAutomation);
            return new((read ?? []).Where(static pair => pair.Value is { MachineId.Length: > 0, Key.Length: > 0 }), StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Without the record, every placed automation is copied again: the machines take the same copy as a no-op.
            return new(StringComparer.Ordinal);
        }
    }

    // Under the gate.
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var staged = _path + ".tmp";
            File.WriteAllText(staged, JsonSerializer.Serialize(_placed, PlacedAutomationJsonContext.Default.DictionaryStringPlacedAutomation));
            File.Move(staged, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log("The record of the placed automations could not be written: " + exception.Message);
        }
    }

    private void Log(string message)
    {
        if (LogManager.IsInitialized) _logger.Info(message);
    }
}

/// <summary>What was copied to a machine.</summary>
/// <param name="MachineId">The machine.</param>
/// <param name="Key">What it was given (the definition, whether enabled, the repository of its project), hashed.</param>
/// <param name="CopiedAt">When.</param>
internal sealed record PlacedAutomation(string MachineId, string Key, DateTimeOffset CopiedAt)
{
    /// <summary>Gets the number of the version signed for it.</summary>
    public long Number { get; init; }

    /// <summary>Gets the server link it was given through; null for a paired machine on a direct channel.</summary>
    public string? Via { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, PlacedAutomation>))]
[JsonSerializable(typeof(Dictionary<string, long>))]
internal sealed partial class PlacedAutomationJsonContext : JsonSerializerContext;
