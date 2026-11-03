using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Automations;
using CodeAlta.Catalog.SessionMoves;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
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
    private readonly Dictionary<string, long> _numbers = new(StringComparer.Ordinal);
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
        _repositoryOf = repositoryOf ?? RepositoryKey.ReadAsync;
        _placed = Load(_path);
    }

    /// <summary>Raised, on any thread, when where an automation stands on its machine, or its runs there, changed.</summary>
    internal event Action? Changed;

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

        return new(machine, _machines(machine)?.Machine.Name, "pending", "It is copied to the machine in a moment.", null);
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
                if (placed.TryGetValue(id, out var entry) && entry.Definition.RunsOn == record.MachineId) continue;
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
            if (_placed.TryGetValue(entry.Id, out var record) && record.MachineId == machineId && record.Key == key)
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
            if (_runsReadAt.TryGetValue(id, out var readAt) && now - readAt < RunsReadEvery && now >= readAt) return false;
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

    private AutomationPlacementStatus PendingStatus(AutomationEntry entry, string machineId, RemoteSessionHost? host)
    {
        if (host is null) return new(machineId, null, "failed", "No paired machine has this id: choose another machine, or This computer.", null);
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
        };

    // What the machine was given: the definition, whether it is enabled, and the repository of its project, as a signed version.
    private static string Key(AutomationDefinition copy, string? repository)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            "signed\n" + copy.Fingerprint() + "\n" + (copy.Enabled ? "1" : "0") + "\n" + repository)))[..32];

    /// <summary>The kinds of the triggers of an automation, the facts a version says in clear.</summary>
    internal static IReadOnlyList<string> TriggerKinds(AutomationDefinition definition)
        => [.. definition.Triggers.Select(static trigger => trigger.Kind.ToString().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Take(FleetAutomationVersionDocuments.MaxTriggers)];

    // A number above every one this computer signed for the automation, and above the time: a record lost here still numbers after what the machine holds.
    private long NextNumber(string id)
    {
        lock (_gate)
        {
            var held = Math.Max(_numbers.GetValueOrDefault(id), _placed.TryGetValue(id, out var record) ? record.Number : 0);
            return Math.Max(held + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, PlacedAutomation>))]
internal sealed partial class PlacedAutomationJsonContext : JsonSerializerContext;
