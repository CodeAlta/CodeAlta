using System.Text.Json;
using CodeAlta.Automations;
using CodeAlta.Core.IO;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;

namespace CodeAlta.Desktop.Fleet;

/// <summary>A node of a server, as a pool chooses its members by their tags.</summary>
/// <param name="Id">Its device.</param>
/// <param name="Name">Its name, as the server lists it.</param>
/// <param name="LinkId">The server link that lists it.</param>
/// <param name="Tags">Its tags, as it publishes them.</param>
internal sealed record DesktopPoolMachine(string Id, string Name, string LinkId, IReadOnlyList<string> Tags)
{
    /// <summary>Gets whether the machine is connected to its server now.</summary>
    public bool Online { get; init; }

    /// <summary>Gets how many sessions work on it, as it told its server; null when it did not say.</summary>
    public int? Working { get; init; }

    /// <summary>Gets how many runs wait for one of its seats; null when it did not say.</summary>
    public int? Queued { get; init; }

    /// <summary>Gets how many sessions it lets work at once; null for no limit.</summary>
    public int? SessionLimit { get; init; }

    /// <summary>Gets how many more sessions it starts at once now; large without a limit, below zero when its queue is long.</summary>
    internal int FreeSeats => (SessionLimit ?? 1_000_000) - (Working ?? 0) - (Queued ?? 0);

    /// <summary>Gets whether the machine has all of the tags, compared without case.</summary>
    internal bool HasAll(IEnumerable<string> tags)
    {
        var held = Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tags.All(held.Contains);
    }
}

/// <summary>The nodes of the servers of this computer, and the links of the servers online.</summary>
/// <param name="Machines">The nodes.</param>
/// <param name="OnlineLinks">The server links connected now: a machine leaves a pool only as one of them says.</param>
internal sealed record DesktopPoolDirectory(IReadOnlyList<DesktopPoolMachine> Machines, IReadOnlySet<string> OnlineLinks);

/// <summary>The members of a pool, as resolved now.</summary>
/// <param name="Members">The machines of the person (or of the scope it is published to) that have all the tags.</param>
/// <param name="Problem">Why the pool has no member, or cannot be one; null when it has members.</param>
/// <param name="Unsure">Whether a machine could not be checked: none leaves the pool on this look.</param>
internal sealed record DesktopPoolMembers(IReadOnlyList<DesktopPoolMachine> Members, string? Problem, bool Unsure);

/// <summary>A copy of an automation given to one machine of its pool.</summary>
/// <param name="AutomationId">The automation.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Key">What it was given, hashed.</param>
/// <param name="CopiedAt">When.</param>
internal sealed record PooledCopy(string AutomationId, string MachineId, string Key, DateTimeOffset CopiedAt)
{
    /// <summary>Gets the number of the version signed for it.</summary>
    public long Number { get; init; }

    /// <summary>Gets the server link it was given through.</summary>
    public string? Via { get; init; }
}

// The automations that run on the machines with some tags (runs_on = { tags = [...] }): every machine of the server that has
// them gets a copy signed and sealed for it, which says the tags; each machine claims each due time or event at the server,
// which grants it to one. The copies are recorded by automation and machine, apart from those placed on one machine.
internal sealed partial class DesktopAutomationPlacement
{
    /// <summary>The name of the record of the copies given to the machines of pools, in the fleet folder of the state root.</summary>
    public const string PoolFileName = "pooled-automations.json";

    // The copies given to the machines of pools, by automation and machine. Under the gate.
    private Dictionary<(string Id, string MachineId), PooledCopy>? _pool;
    private readonly Dictionary<(string Id, string MachineId), AutomationPlacementStatus> _memberStatus = [];

    /// <summary>
    /// Gets what lists the nodes of the servers of this computer with their tags, from which the members of a pool are chosen;
    /// null for none: an automation that runs on tags then has no member.
    /// </summary>
    internal Func<DesktopPoolDirectory>? PoolMachines { get; init; }

    /// <summary>Gets the time zone the copies of a pool read their schedules in when the automation names none; tests set one.</summary>
    internal TimeZoneInfo PoolTimeZone { get; init; } = TimeZoneInfo.Local;

    private string PoolPath => Path.Combine(Path.GetDirectoryName(_path)!, PoolFileName);

    // Under the gate.
    private Dictionary<(string Id, string MachineId), PooledCopy> Pool => _pool ??= LoadPool(PoolPath);

    /// <summary>
    /// Resolves the members of a pool now: the nodes of the servers of this computer that have all the tags, and that the
    /// person gives automations to (their own machines, or those of the team or organization it is published to).
    /// </summary>
    /// <param name="tags">The tags.</param>
    /// <param name="scope">The scope it is published to; null for the person's own.</param>
    /// <param name="cancellationToken">Cancels the checks.</param>
    /// <returns>The members, and why there is none.</returns>
    internal async Task<DesktopPoolMembers> PoolMembersAsync(IReadOnlyList<string> tags, string? scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var named = string.Join(", ", tags);
        if (PoolMachines?.Invoke() is not { OnlineLinks.Count: > 0 } directory)
            return new([], "Machines with tags run it through a CodeAlta Server, which claims each of its runs: connect this computer to the server of the machines.", true);
        var members = new List<DesktopPoolMachine>();
        var unsure = false;
        foreach (var machine in directory.Machines.Where(machine => machine.HasAll(tags)))
        {
            if (ServerRoute?.Invoke(machine.Id) is not { } route) continue;
            using (route)
            {
                try
                {
                    if (scope is null ? await route.IsOwnMachineAsync(machine.Id, cancellationToken).ConfigureAwait(false) : route.CheckScope(scope) is null)
                        members.Add(machine with { LinkId = route.LinkId });
                }
                catch (Exception exception) when (exception is FleetDeviceListException or FleetServerException or HttpRequestException or IOException
                    or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    unsure = true;
                }
            }
        }

        if (members.Select(static member => member.LinkId).Distinct(StringComparer.Ordinal).Count() > 1)
            return new(members, "Machines with these tags are on more than one server: a pool is the machines of one server, which claims each of its runs. Give them other tags.", true);
        return new(members, members.Count > 0 ? null
            : $"No machine of yours has the tag{(tags.Count > 1 ? "s" : "")} {named}: add {(tags.Count > 1 ? "them" : "it")} on the page of a machine, in Settings > Fleet > Machines, or with altanode tags add.",
            unsure);
    }

    // Under _work. Gives each pooled automation to the members of its pool, reads where it stands and its runs, and records
    // the copies of the machines that left a pool to be taken away there. Returns whether something changed.
    private async Task<bool> SyncPoolsAsync(IReadOnlyList<AutomationEntry> pooled, CancellationToken cancellationToken)
    {
        var changed = false;
        var wanted = new HashSet<(string Id, string MachineId)>();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var directory = PoolMachines?.Invoke();
        foreach (var entry in pooled)
        {
            var resolved = await PoolMembersAsync(entry.Definition.RunsOnTags!, entry.Definition.PublishTo, cancellationToken).ConfigureAwait(false);
            if (resolved.Unsure) kept.Add(entry.Id);
            var members = resolved.Members.Select(static member => member.LinkId).Distinct(StringComparer.Ordinal).Count() > 1 ? [] : resolved.Members;
            // The server of its members is the arbiter of its runs: copies through another server (one the desktop no longer
            // reaches, say) are taken away first, and the members wait until those machines answer, so that two servers never
            // both grant its due times.
            if (members.Count > 0 && members[0].LinkId is var link)
            {
                PooledCopy[] elsewhere;
                lock (_gate) elsewhere = [.. Pool.Values.Where(copy => copy.AutomationId == entry.Id && copy.Via != link)];
                foreach (var copy in elsewhere)
                {
                    QueuePoolRemoval(copy);
                    changed = true;
                }
            }

            var statuses = new List<AutomationPlacementStatus>(members.Count);
            foreach (var member in members)
            {
                wanted.Add((entry.Id, member.Id));
                if (ServerRoute?.Invoke(member.Id) is not { } route)
                {
                    statuses.Add(SetMember(entry.Id, new(member.Id, member.Name, "pending", "Its server is not connected: it is given when it is.", null)));
                    continue;
                }

                using (route) statuses.Add(await CopyToMemberAsync(entry, member, route, cancellationToken).ConfigureAwait(false));
            }

            changed |= await ReadPoolAsync(entry, members, cancellationToken).ConfigureAwait(false);
            lock (_gate) statuses = [.. members.Select(member => _memberStatus.GetValueOrDefault((entry.Id, member.Id)) ?? statuses.First(status => status.MachineId == member.Id))];
            changed |= SetPoolStatus(entry, statuses, resolved.Problem);
        }

        // A machine that left a pool (it lost a tag, the automation runs elsewhere now or was deleted) has its copy taken away,
        // as a server online says; one whose server is away keeps it until it says.
        KeyValuePair<(string Id, string MachineId), PooledCopy>[] copies;
        lock (_gate) copies = [.. Pool];
        foreach (var (key, copy) in copies)
        {
            if (wanted.Contains(key) || kept.Contains(key.Id)) continue;
            var stillPooled = pooled.Any(entry => entry.Id == key.Id);
            if (stillPooled && (copy.Via is null || directory?.OnlineLinks.Contains(copy.Via) != true)) continue;
            QueuePoolRemoval(copy);
            changed = true;
        }

        lock (_gate)
        {
            foreach (var id in _status.Where(pair => pair.Value.Members is not null && !pooled.Any(entry => entry.Id == pair.Key)).Select(static pair => pair.Key).ToArray())
                _status.Remove(id);
        }

        return changed;
    }

    // Under _work. Records the copy of a machine that left the pool to be taken away there, with the number of its removal reserved now.
    private void QueuePoolRemoval(PooledCopy copy)
    {
        var number = NextNumber(copy.AutomationId);
        lock (_gate)
        {
            var key = (copy.AutomationId, copy.MachineId);
            var name = _memberStatus.GetValueOrDefault(key)?.MachineName;
            var previous = _removals.GetValueOrDefault(key);
            _removals[key] = new PendingAutomationRemoval(copy.AutomationId, copy.MachineId, DateTimeOffset.UtcNow) { Number = number, Via = copy.Via, MachineName = name, Pooled = true };
            if (!SaveRemovals())
            {
                if (previous is null) _removals.Remove(key);
                else _removals[key] = previous;
                return;
            }

            Pool.Remove(key);
            _memberStatus.Remove(key);
            SavePool();
        }

        Log($"The automation {copy.AutomationId} is taken away from {copy.MachineId}, which left its pool.");
    }

    // Under _work. Gives a pooled automation to one member through its server, unless the server holds it as it is now.
    private async Task<AutomationPlacementStatus> CopyToMemberAsync(AutomationEntry entry, DesktopPoolMachine member, DesktopServerAutomationRoute route, CancellationToken cancellationToken)
    {
        var (machineId, machineName) = (member.Id, route.MachineName);
        if (entry.Problem is { } problem) return SetMember(entry.Id, new(machineId, machineName, "failed", problem, null));
        string? repository = null;
        if (entry.ProjectPath is { } folder)
        {
            repository = await _repositoryOf(folder, cancellationToken).ConfigureAwait(false);
            if (repository is null) return SetMember(entry.Id, new(machineId, machineName, "failed", "Its project has no remote repository that the other machine could find.", null));
        }

        // Every machine of a pool reads its schedules in the same time zone, so that each due time is the same instant on all:
        // the one it names, else the one of this computer.
        var copy = Copy(entry) with { TimeZone = entry.Definition.TimeZone ?? AutomationDefinition.ZoneId(PoolTimeZone) };
        var scope = entry.Definition.PublishTo;
        if (route.CheckScope(scope) is { } outOfScope) return SetMember(entry.Id, new(machineId, machineName, "failed", outOfScope, null));
        if (scope is not null && copy.Model.Provider is null)
            return SetMember(entry.Id, new(machineId, machineName, "failed",
                "An automation of a team or of the organization names the provider it runs with: choose one of the machine that runs with an API key or a gateway.", null));
        var tags = entry.Definition.RunsOnTags!;
        var key = Key(copy, repository) + "|pool:" + string.Join(',', tags) + (scope is null ? string.Empty : "|" + scope);
        lock (_gate)
        {
            if (Pool.TryGetValue((entry.Id, machineId), out var record) && record.Key == key && record.Via == route.LinkId)
                return _memberStatus.TryGetValue((entry.Id, machineId), out var known)
                    ? known
                    : SetMember(entry.Id, new(machineId, machineName, "pending", $"Given to the server: {machineName} takes it when it connects.", null));
        }

        // A copy that its server does not arbitrate as one of this pool (reached directly, given to one machine, or through another
        // server) may run without this server's claim: the pool waits until its machine took it away.
        if (copy.Enabled && PoolHandoff(entry.Id, machineId, machineName, route.LinkId) is { } waiting) return SetMember(entry.Id, waiting);
        var number = NextNumber(entry.Id);
        try
        {
            var sealedText = await route.SealAsync(new FleetAutomationContent(AutomationConfig.Write(string.Empty, copy), repository), entry.Id, number, machineId, scope,
                cancellationToken).ConfigureAwait(false);
            // A version of a pool is of a format a machine that does not know pools refuses, rather than run it without the claim of its server.
            var signed = _sign(new FleetAutomationVersion(FleetAutomationVersion.VersionFor(copy.Autonomy is not null, pooled: true), route.OrganizationId, entry.Id, number, machineId, false,
                copy.Enabled, TriggerKinds(copy), null, null, sealedText, _deviceId(), string.Empty, DateTimeOffset.UtcNow)
            {
                Scope = scope, Provider = scope is null ? null : copy.Model.Provider, Tags = tags,
            });
            await route.PutAsync(signed, cancellationToken).ConfigureAwait(false);
            lock (_gate) _numbers[entry.Id] = number;
        }
        catch (ArgumentException exception)
        {
            return SetMember(entry.Id, new(machineId, machineName, "failed", exception.Message, null));
        }
        catch (FleetServerException exception) when (exception.Status is System.Net.HttpStatusCode.Forbidden)
        {
            return SetMember(entry.Id, new(machineId, machineName, "failed", $"{machineName} is not one of your machines: you give automations to the machines you own.", null));
        }
        catch (FleetServerException exception) when ((int)exception.Status is >= 400 and < 500)
        {
            return SetMember(entry.Id, new(machineId, machineName, "failed", exception.Message, null));
        }
        catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or FleetDeviceListException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return SetMember(entry.Id, new(machineId, machineName, "pending", "The server did not answer: it is given when it does.", null));
        }

        lock (_gate)
        {
            Pool[(entry.Id, machineId)] = new PooledCopy(entry.Id, machineId, key, DateTimeOffset.UtcNow) { Number = number, Via = route.LinkId };
            _runsReadAt.Remove(entry.Id);
            SavePool();
        }

        Log($"The automation '{entry.Definition.Name}' ({entry.Id}) was given to {machineName}, a machine of its pool, through its server.");
        return SetMember(entry.Id, new(machineId, machineName, "pending", $"Given to the server: {machineName} takes it when it connects.", null));
    }

    // Why a pooled automation, enabled, is not given yet: a copy that left a machine and may still run there without the claim
    // of this pool's server (one reached directly, one given to that machine alone, which runs alone while its server is away,
    // or one of a pool of another server). Among the machines of the pool's server, the claim decides: the removals of members
    // that left it are not waited for.
    private AutomationPlacementStatus? PoolHandoff(string id, string machineId, string machineName, string link)
    {
        PendingAutomationRemoval[] before;
        lock (_gate)
        {
            if (_removalsUnread is { } unread && !Pool.ContainsKey((id, machineId))) return new(machineId, machineName, "waiting", Unreadable(unread), null);
            before = [.. _removals.Values.Where(removal => removal.AutomationId == id && !removal.Forced && !(removal.Pooled && removal.Via == link))];
        }

        if (before.Length == 0) return null;
        var names = string.Join(", ", before.Select(static removal => removal.MachineName ?? removal.MachineId).Distinct(StringComparer.Ordinal));
        return new(machineId, machineName, "waiting",
            $"It waits until {names} takes away its copy, so that both machines never run it: it is given to its pool once {names} answers, or now if you start it anyway." + Refusals(before),
            null);
    }

    // Under _work. Reads, through the server of the pool, where each copy stands and the runs of all the machines, at most every so often.
    private async Task<bool> ReadPoolAsync(AutomationEntry entry, IReadOnlyList<DesktopPoolMachine> members, CancellationToken cancellationToken)
    {
        var id = entry.Id;
        if (members.Count == 0) return SetRead(id, Runs(id), null);
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_runsReadAt.TryGetValue(id, out var readAt) && now - readAt < RunsReadInterval && now >= readAt) return false;
            _runsReadAt[id] = now;
        }

        if (ServerRoute?.Invoke(members[0].Id) is not { } route) return false;
        AutomationListDto list;
        IReadOnlyList<AutomationRun> runs;
        using (route)
        {
            try
            {
                list = await route.ListAsync(cancellationToken).ConfigureAwait(false);
                runs = [.. (await route.ListRunsAsync(id, RunsKept, cancellationToken).ConfigureAwait(false)).Runs.Select(run => new AutomationRun(run.RunId, run.AutomationId, run.StartedAt, run.Trigger)
                {
                    Name = entry.Definition.Name, SessionId = run.SessionId, Status = run.Status == "claimed" ? AutomationRun.Running : run.Status, Message = run.Message, EndedAt = run.EndedAt,
                    InputTokens = run.InputTokens, OutputTokens = run.OutputTokens, Cost = run.Cost, CostUnit = run.CostUnit, MachineId = run.MachineId,
                })];
            }
            catch (Exception exception) when (exception is FleetServerException or HttpRequestException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                lock (_gate) _runsReadAt.Remove(id);
                return false;
            }
        }

        var changed = false;
        DateTimeOffset? nextDue = null;
        foreach (var member in members)
        {
            var held = list.Automations.FirstOrDefault(item => item.Id == id && item.MachineId == member.Id);
            if (held is { Removed: false, NextDue: { } due } && (nextDue is null || due < nextDue)) nextDue = due;
            PooledCopy? record;
            lock (_gate) record = Pool.GetValueOrDefault((id, member.Id));
            if (record is null) continue;
            var status = held switch
            {
                { Number: var number, State: "applied" } when number == record.Number => new AutomationPlacementStatus(member.Id, member.Name, "copied", null, record.CopiedAt),
                { Number: var number, State: "refused" } when number == record.Number => new AutomationPlacementStatus(member.Id, member.Name, "failed",
                    $"{member.Name} refused it: {held.Message ?? "no reason given"}.", null),
                null => new AutomationPlacementStatus(member.Id, member.Name, "pending", "The server no longer holds it: it is given again.", null),
                _ => new AutomationPlacementStatus(member.Id, member.Name, "pending", $"Given to the server: {member.Name} takes it when it connects.", null),
            };
            if (held is null)
            {
                lock (_gate)
                {
                    Pool.Remove((id, member.Id));
                    SavePool();
                }
            }

            lock (_gate) changed |= !Equals(_memberStatus.GetValueOrDefault((id, member.Id)), status);
            SetMember(id, status);
        }

        // A removal the machine took away is forgotten by the server, once no copy of this computer is recorded there.
        foreach (var item in list.Automations.Where(static item => item is { Removed: true, State: "applied" }))
        {
            lock (_gate)
                if (!Pool.ContainsKey((item.Id, item.MachineId)) && !(_placed.TryGetValue(item.Id, out var placed) && placed.MachineId == item.MachineId)) _forget.Add((item.Id, item.MachineId));
        }

        return SetRead(id, runs, nextDue) || changed;
    }

    // Runs a pooled automation now on the member with the most free seats, through its server.
    private async Task<AutomationRun> RunInPoolAsync(AutomationEntry entry, bool dryRun, Func<string, AutomationRun> failed, CancellationToken cancellationToken)
    {
        // The machines run the copy they hold: a change not given yet goes first, and only a machine that took the copy of
        // this definition is chosen (a copy given again is pending until its machine takes it), so that an older one never runs.
        await SyncAsync(cancellationToken).ConfigureAwait(false);
        var resolved = await PoolMembersAsync(entry.Definition.RunsOnTags!, entry.Definition.PublishTo, cancellationToken).ConfigureAwait(false);
        if (resolved.Members.Select(static member => member.LinkId).Distinct(StringComparer.Ordinal).Count() > 1) return failed(resolved.Problem!);
        DesktopPoolMachine? best;
        lock (_gate)
        {
            best = ChooseMember(resolved.Members, member => _memberStatus.GetValueOrDefault((entry.Id, member.Id))?.State == "copied" && Pool.ContainsKey((entry.Id, member.Id)));
        }

        if (best is null) return failed(resolved.Problem ?? "No machine of its pool holds it as it is now: it runs once one took its last change.");
        if (ServerRoute?.Invoke(best.Id) is not { } route) return failed($"The server of {best.Name} is not connected.");
        using (route)
        {
            var run = await RunThroughServerAsync(entry, best.Id, route, failed, dryRun, cancellationToken, pooled: true).ConfigureAwait(false);
            return run with { MachineId = run.MachineId ?? best.Id };
        }
    }

    /// <summary>
    /// Chooses the member of a pool a run asked by hand goes to: one that holds its copy, online first, then the one with the
    /// most free seats, then the one with the fewest sessions at work.
    /// </summary>
    /// <param name="members">The members.</param>
    /// <param name="holds">Whether a member holds its copy as it is now.</param>
    /// <returns>The member; null when none holds it.</returns>
    internal static DesktopPoolMachine? ChooseMember(IEnumerable<DesktopPoolMachine> members, Func<DesktopPoolMachine, bool> holds)
        => members.Where(holds).OrderByDescending(static member => member.Online).ThenByDescending(static member => member.FreeSeats).ThenBy(static member => member.Working ?? 0)
            .ThenBy(static member => member.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    // Where a pooled automation stands: the sum of its members.
    private bool SetPoolStatus(AutomationEntry entry, IReadOnlyList<AutomationPlacementStatus> members, string? problem)
    {
        var state = members.Count == 0 ? "failed"
            : members.All(static member => member.State == "copied") ? "copied"
            : members.Any(static member => member.State is "pending" or "waiting") ? "pending"
            : members.Any(static member => member.State == "copied") ? "copied"
            : "failed";
        var first = members.FirstOrDefault(static member => member.State != "copied");
        var message = problem ?? (first is null ? null : $"{first.MachineName ?? first.MachineId}: {first.Message}");
        var copiedAt = members.Where(static member => member.CopiedAt is not null).Select(static member => member.CopiedAt).Max();
        var status = new AutomationPlacementStatus(string.Empty, null, state, message, copiedAt) { Members = members };
        lock (_gate)
        {
            if (_status.TryGetValue(entry.Id, out var known) && known.Members is { } before && known with { Members = null } == status with { Members = null }
                && before.SequenceEqual(members)) return false;
            _status[entry.Id] = status;
            return true;
        }
    }

    // Where a pooled automation stands for this computer before its first look: given in a moment.
    private AutomationPlacementStatus PoolStatus(AutomationEntry entry)
    {
        lock (_gate)
        {
            if (_status.TryGetValue(entry.Id, out var status) && status.Members is not null) return status;
        }

        return new(string.Empty, null, "pending", $"It is given to the machines with the tags {string.Join(", ", entry.Definition.RunsOnTags!)} in a moment.", null) { Members = [] };
    }

    private AutomationPlacementStatus SetMember(string id, AutomationPlacementStatus status)
    {
        lock (_gate) _memberStatus[(id, status.MachineId)] = status;
        return status;
    }

    private static Dictionary<(string Id, string MachineId), PooledCopy> LoadPool(string path)
    {
        var pool = new Dictionary<(string Id, string MachineId), PooledCopy>();
        try
        {
            if (!File.Exists(path)) return pool;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var copy in JsonSerializer.Deserialize(stream, PlacedAutomationJsonContext.Default.ListPooledCopy) ?? [])
            {
                if (copy is { AutomationId.Length: > 0, MachineId.Length: > 0, Key.Length: > 0 }) pool[(copy.AutomationId, copy.MachineId)] = copy;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Without the record, every member is given its copy again: the machines take the same copy as a newer version.
        }

        return pool;
    }

    // Under the gate.
    private void SavePool()
    {
        try
        {
            var path = PoolPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var staged = path + ".tmp";
            File.WriteAllText(staged, JsonSerializer.Serialize([.. Pool.Values], PlacedAutomationJsonContext.Default.ListPooledCopy));
            StagedFileReplacement.MoveOver(staged, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log("The record of the automations given to pools could not be written: " + exception.Message);
        }
    }
}
