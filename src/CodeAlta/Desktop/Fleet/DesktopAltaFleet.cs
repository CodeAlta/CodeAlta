using CodeAlta.Automations;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Desktop.Fleet;

/// <summary>
/// <c>alta fleet</c> in CodeAlta Desktop: this computer and its machines (<see cref="DesktopFleet"/>), the servers it is
/// linked to (<see cref="DesktopFleetServers"/>), the moves (<see cref="DesktopMoves"/>) and where the automations run
/// (<see cref="DesktopAutomationPlacement"/>), as the window shows them. It reads what this computer may see, and changes
/// only where an automation runs; access stays with the person.
/// </summary>
/// <param name="fleet">The machines of this computer.</param>
/// <param name="machineName">The name of this computer.</param>
/// <param name="version">The version of CodeAlta this computer runs.</param>
internal sealed class DesktopAltaFleet(DesktopFleet fleet, string machineName, string? version) : IAltaFleet
{
    /// <summary>Gets the servers this computer is linked to; null for none.</summary>
    internal DesktopFleetServers? Servers { get; init; }

    /// <summary>Gets the moves of sessions; null when this computer moves none.</summary>
    internal DesktopMoves? Moves { get; init; }

    /// <summary>Gets the automations of the person; null for none.</summary>
    internal AutomationService? Automations { get; init; }

    /// <summary>Gets where the automations placed on machines stand; null when none can be placed.</summary>
    internal DesktopAutomationPlacement? Placement { get; init; }

    /// <summary>Gets the runtime of this computer, which says whether a session runs here; null when it is not asked.</summary>
    internal CodeAltaHost? Host { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<AltaFleetMachine> ListMachines()
    {
        var (machines, presence) = Directory();
        var self = Servers?.DeviceId;
        var thisComputer = new AltaFleetMachine(string.Empty, machineName, "online", "ready")
        {
            IsThisComputer = true,
            Os = CodeAlta.Fleet.Channels.FleetChannelProtocol.OperatingSystemName,
            Version = version,
            Capabilities = self is not null && machines.TryGetValue(self, out var own) ? [.. own.Inventory?.Capabilities ?? []] : [],
            Labels = self is not null && machines.TryGetValue(self, out own) ? [.. own.Inventory?.Labels ?? []] : [],
        };
        return [thisComputer, .. fleet.Hosts.Select(host => Describe(host, machines.GetValueOrDefault(host.MachineId!)?.Inventory, presence.GetValueOrDefault(host.MachineId!)))];
    }

    /// <inheritdoc />
    public async Task<AltaFleetMachineDetail> ShowMachineAsync(string machine, CancellationToken cancellationToken)
    {
        if (IsThisComputer(machine)) return new("ok", null, ListMachines()[0]);
        if (Resolve(machine) is not { } host) return new("not_found", null, null);
        CodeAlta.Fleet.NodeApi.FleetNodeInfo info;
        try { info = await host.ReadInfoAsync(cancellationToken).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), host.Problem, Describe(host)); }
        catch (FleetException exception) { return new(exception.Error.Code, exception.Error.Message, Describe(host)); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, exception.Message, Describe(host)); }

        var update = info.Update;
        return new("ok", null, Describe(host))
        {
            Projects = [.. info.Projects.OrderBy(static project => project.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(static project => new AltaFleetProject(project.Id, project.Name, project.RepositoryKey))],
            Providers = [.. info.Providers.Select(static provider => new AltaFleetProvider(provider.Key, provider.Name, provider.IsDefault, provider.DefaultModelId,
                [.. provider.Models.Select(static model => model.Id)]))],
            Update = update is null ? null : new AltaFleetUpdateState(update.Running, update.Installed, update.Pending?.Version, update.Pending?.Phase,
                update.LastOutcome is { } last ? $"{last.Outcome} {last.Version}" : null),
        };
    }

    /// <inheritdoc />
    public Task<AltaFleetSessions> ListSessionsAsync(string machine, int limit, CancellationToken cancellationToken)
    {
        if (IsThisComputer(machine)) return Task.FromResult(new AltaFleetSessions("this_computer", "`alta session list` lists the sessions of this computer.", machineName, []));
        if (Resolve(machine) is not { } host) return Task.FromResult(new AltaFleetSessions("not_found", null, null, []));
        var listing = host.Listing;
        // A machine never reached has no list yet; one offline keeps its last one.
        if (listing.ListedAt is null && host.State != RemoteMachineState.Online)
            return Task.FromResult(new AltaFleetSessions(RemoteSessionHost.Describe(host.State), host.Problem, Name(host), []));
        return Task.FromResult(new AltaFleetSessions("ok", null, Name(host), [.. listing.Sessions.Take(Math.Max(1, limit) + 1)
            .Select(static session => new AltaFleetSession(session.Id, session.Title, session.State, session.ProjectId, session.ProviderKey, session.ModelId,
                session.ParentSessionId, session.UpdatedAt))]));
    }

    /// <inheritdoc />
    public async Task<AltaFleetDevices> ListDevicesAsync(string machine, CancellationToken cancellationToken)
    {
        if (IsThisComputer(machine)) return new("not_manager", "This computer is no node: its devices are in Settings > Fleet.", machineName, []);
        if (Resolve(machine) is not { } host) return new("not_found", null, null, []);
        try
        {
            var info = await host.ReadInfoAsync(cancellationToken).ConfigureAwait(false);
            if (!info.Permissions.Contains("manage", StringComparer.Ordinal)) return new("not_manager", null, Name(host), []);
            var devices = await host.ManageAsync((client, token) => client.ListDevicesAsync(token), changes: false, cancellationToken).ConfigureAwait(false);
            var self = fleet.DeviceId;
            return new("ok", null, Name(host), [.. devices.Devices.OrderBy(static device => device.RevokedAt is null ? 0 : 1).ThenBy(static device => device.PairedAt)
                .Select(device => new AltaFleetDevice(device.DeviceId, device.Name, [.. device.Permissions], device.PairedAt, device.RevokedAt,
                    string.Equals(device.DeviceId, self, StringComparison.Ordinal)))]);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), host.Problem, Name(host), []); }
        catch (FleetException exception) { return new(exception.Error.Code == FleetErrorCodes.Forbidden ? "not_manager" : exception.Error.Code, exception.Error.Message, Name(host), []); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, exception.Message, Name(host), []); }
    }

    /// <inheritdoc />
    public async Task<AltaFleetMoveStatus> GetMoveStatusAsync(string sessionId, string? machine, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        // Where it runs: this computer, unless it gave it away, or the machine that lists it.
        var here = Host is { } local && await local.RuntimeService.TryGetRecoverableSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) is not null
            && local.RuntimeService.Relinquishments.Read(sessionId) is not { IsRelinquished: true };
        var source = here ? null : fleet.Hosts.FirstOrDefault(host => host.Lists(sessionId));
        if (!here && source is null) return new("session_missing", "The session runs neither here nor on a machine that is online.", sessionId, null);
        var status = new AltaFleetMoveStatus("ok", null, sessionId, here ? machineName : Name(source!)) { RunsHere = here };
        if (machine is null) return status;

        string? targetId;
        string targetName;
        if (IsThisComputer(machine)) (targetId, targetName) = (null, machineName);
        else if (Resolve(machine) is { } target) (targetId, targetName) = (target.MachineId, Name(target));
        else return new("machine_not_found", null, sessionId, status.Machine);
        if (string.Equals(targetId, source?.MachineId, StringComparison.Ordinal)) return status with { Status = "same_machine", Message = "The session runs there already.", Target = targetName };
        if (Moves is not { } moves) return status with { Status = "unavailable", Message = "This computer moves no session.", Target = targetName };
        try
        {
            var preview = await moves.PreviewAsync(sessionId, source?.MachineId, targetId, cancellationToken).ConfigureAwait(false);
            var from = preview.Preview;
            var to = preview.Target;
            return status with
            {
                Status = from.Status,
                Target = targetName,
                Busy = from.Busy,
                Jobs = [.. from.Jobs],
                StaysBehind = [.. from.Secrets],
                TargetStatus = preview.TargetRefusal ?? to?.Status,
                TargetCode = to?.Code,
                Message = to?.Message,
                Project = to?.ProjectName,
                CanClone = to?.CanClone ?? false,
                Warnings = [.. to?.Warnings ?? []],
            };
        }
        catch (KeyNotFoundException) { return status with { Status = "machine_not_found", Target = targetName }; }
        catch (ObjectDisposedException exception) { return status with { Status = RemoteSessionHost.MachineOfflineCode, Message = exception.Message, Target = targetName }; }
        catch (FleetException exception) { return status with { Status = exception.Error.Code, Message = exception.Error.Message, Target = targetName }; }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return status with { Status = RemoteSessionHost.MachineOfflineCode, Message = exception.Message, Target = targetName };
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AltaFleetAutomationPlacement> ListAutomationPlacements()
    {
        if (Automations is not { } automations) return [];
        return [.. automations.Snapshot.Entries.Select(entry =>
        {
            if (entry.Definition.RunsOn is null || Placement is not { } placement)
                return new AltaFleetAutomationPlacement(entry.Id, entry.Definition.Name, entry.Definition.Enabled, null, null, "here", null) { NextRunAt = automations.NextDue(entry.Id) };
            var status = placement.Status(entry)!;
            return new AltaFleetAutomationPlacement(entry.Id, entry.Definition.Name, entry.Definition.Enabled, status.MachineId, status.MachineName, status.State, status.Message)
            {
                CopiedAt = status.CopiedAt,
                NextRunAt = placement.NextDue(entry.Id),
            };
        })];
    }

    /// <inheritdoc />
    public IReadOnlyList<AltaAutomationRun> ListPlacedRuns(string? automationId, int limit)
    {
        if (Placement is not { } placement || Automations is not { } automations) return [];
        var ids = automationId is not null ? [automationId]
            : automations.Snapshot.Entries.Where(static entry => entry.Definition.RunsOn is not null).Select(static entry => entry.Id).ToArray();
        return [.. ids.SelectMany(placement.Runs).OrderByDescending(static run => run.StartedAt).Take(Math.Max(1, limit))
            .Select(static run => new AltaAutomationRun(run.Id, run.AutomationId, run.Name, run.SessionId, run.ProjectId, run.StartedAt, run.EndedAt, run.Trigger,
                run.Detail, run.Status, run.Message))];
    }

    /// <inheritdoc />
    public async Task<AltaFleetChange> PlaceAutomationAsync(string automationId, string? machine, CancellationToken cancellationToken)
    {
        if (Automations is not { } automations || Placement is null) return new("unavailable", "This computer places no automation on a machine.");
        if (automations.Snapshot.Find(automationId) is not { } entry) return new("not_found");
        if (!entry.Source.IsGlobal || entry.Source.IsAssigned)
            return new("refused", "Only an automation of the configuration of the user runs on another machine: this one is kept with its project, or another device assigned it here.");
        string? runsOn = null;
        if (machine is not null && !IsThisComputer(machine))
        {
            if (Resolve(machine) is not { } host) return new("machine_not_found");
            // What the machine says this computer may do there, once it said it.
            if (!(host.Listing.Info?.Permissions ?? host.Machine.Permissions).Contains("automate", StringComparer.Ordinal))
                return new("not_allowed", $"{Name(host)} does not let this computer automate: its owner gives the `automate` permission in the page of the machine.");
            runsOn = host.MachineId;
        }

        if (string.Equals(entry.Definition.RunsOn, runsOn, StringComparison.Ordinal)) return new("ok");
        var refused = await automations.SaveAsync(entry.Definition with { RunsOn = runsOn }, null, cancellationToken).ConfigureAwait(false);
        Placement.Poke();
        return refused is null ? new("ok") : new("refused", refused);
    }

    /// <inheritdoc />
    public async Task<AltaFleetStatistics> QueryStatisticsAsync(AltaFleetStatisticsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Servers is not { } servers || Connection(query.Server) is not { } connection) return new("no_server", null, null, []);
        try
        {
            var (response, projects) = await servers.QueryStatisticsAsync(connection.Link.Id,
                new StatisticsQueryRequest(query.From, query.To, query.TimeZone, query.GroupBy.Count == 0 ? null : query.GroupBy, "mine"), cancellationToken).ConfigureAwait(false);
            var names = response.Machines.ToDictionary(static machine => machine.Id, static machine => machine.Name, StringComparer.Ordinal);
            return new("ok", null, Measures(response.Totals), [.. response.Groups.Select(group => new AltaFleetStatisticsGroup(
                group.Machine is { } id ? names.GetValueOrDefault(id) ?? id : null,
                group.Project is { } project ? project.Length == 0 ? "(no project)" : projects.GetValueOrDefault(project) ?? project : null,
                group.Provider, group.Model, group.Period, Measures(group.Measures)))])
            {
                Server = connection.Link.Url,
                Truncated = response.Truncated,
            };
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message, null, []);
        }
    }

    /// <inheritdoc />
    public async Task<AltaFleetUpdates> ReadUpdatesAsync(string? server, CancellationToken cancellationToken)
    {
        if (Servers is not { } servers || Connection(server) is not { } connection) return new("no_server", null);
        try
        {
            var (updates, machines) = await servers.GetUpdatesAsync(connection.Link.Id, cancellationToken).ConfigureAwait(false);
            var view = FleetServerService.UpdatesView(updates, machines);
            return new("ok", null)
            {
                Server = connection.Link.Url,
                ServerVersion = view.ServerVersion,
                Policy = view.Policy.Mode,
                PinnedVersion = view.Policy.PinnedVersion,
                Campaigns = [.. updates.Campaigns.Select(static campaign => new AltaFleetCampaign(campaign.Id, campaign.State, campaign.TargetVersion, campaign.CreatedAt,
                    campaign.Automatic, [.. campaign.Machines.Select(static machine => $"{machine.Name}: {machine.State}")]))],
                Machines = [.. view.Machines.Select(static machine => new AltaFleetMachineVersion(machine.Id, machine.Name, machine.Version, machine.Online))],
            };
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AltaFleetServer> ListServers()
        => Servers is not { } servers ? [] : [.. servers.Connections.Select(static connection => FleetServerService.View(connection, []))
            .Select(static view => new AltaFleetServer(view.Id, view.Url, view.Organization, view.Connection, view.Problem))];

    /// <inheritdoc />
    public async Task<AltaFleetSecrets> ListSecretsAsync(string? machine, CancellationToken cancellationToken)
    {
        if (machine is null || IsThisComputer(machine))
        {
            if (Host is not { } local) return new("unavailable", "This computer keeps no secrets here.", machineName, []);
            try
            {
                var store = new CodeAlta.Catalog.Secrets.LocalSecretStore(local.CatalogOptions.GlobalRoot);
                return new("ok", null, machineName, [.. store.ListSecrets().Select(static secret => new AltaFleetSecret("secret", secret.Name, secret.Version, secret.SetAt)),
                    .. store.ListVariables().Select(static variable => new AltaFleetSecret("variable", variable.Name, variable.Version, variable.SetAt))]);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                return new("unreadable", exception.Message, machineName, []);
            }
        }

        if (Resolve(machine) is not { } host) return new("not_found", null, null, []);
        try
        {
            var list = await host.ManageAsync((client, token) => client.ListSecretsAsync(token), changes: false, cancellationToken).ConfigureAwait(false);
            // The values of the variables come with the list: they stay here.
            return new("ok", null, Name(host), [.. list.Secrets.OrderBy(static secret => secret.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(static secret => new AltaFleetSecret("secret", secret.Name, secret.Version, secret.SetAt)),
                .. list.Variables.OrderBy(static variable => variable.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(static variable => new AltaFleetSecret("variable", variable.Name, variable.Version, variable.SetAt))]);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), host.Problem, Name(host), []); }
        catch (FleetException exception) { return new(exception.Error.Code == FleetErrorCodes.Forbidden ? "not_manager" : exception.Error.Code, exception.Error.Message, Name(host), []); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, exception.Message, Name(host), []); }
    }

    /// <summary>Says whether a machine takes a new session from this computer now.</summary>
    /// <param name="state">Its connection state.</param>
    /// <param name="permissions">What this computer may do there.</param>
    /// <param name="providers">How many of its providers are ready; null when it did not say yet.</param>
    /// <param name="updating">Whether it waits to switch to a new version.</param>
    /// <returns><c>ready</c>, <c>updating</c>, <c>not_allowed</c>, <c>no_provider</c>, or <paramref name="state"/> when it is not online.</returns>
    internal static string Readiness(string state, IReadOnlyCollection<string> permissions, int? providers, bool updating)
        => state != "online" ? state
            : updating ? "updating"
            : !permissions.Contains("start", StringComparer.Ordinal) ? "not_allowed"
            : providers == 0 ? "no_provider"
            : "ready";

    private AltaFleetMachine Describe(RemoteSessionHost host)
    {
        var (machines, presence) = Directory();
        return Describe(host, machines.GetValueOrDefault(host.MachineId!)?.Inventory, presence.GetValueOrDefault(host.MachineId!));
    }

    private static AltaFleetMachine Describe(RemoteSessionHost host, FleetMachineInventory? inventory, FleetPresence? presence)
    {
        var listing = host.Listing;
        var info = listing.Info;
        var state = RemoteSessionHost.Describe(host.State);
        IReadOnlyList<string> permissions = [.. info?.Permissions ?? host.Machine.Permissions];
        IReadOnlyList<string> providers = info is not null ? [.. info.Providers.Select(static provider => provider.Name)] : [.. inventory?.Providers ?? []];
        return new AltaFleetMachine(host.MachineId!, Name(host), state,
            Readiness(state, [.. permissions], info is null && inventory?.Providers is null ? null : providers.Count, info?.Update?.Pending is not null))
        {
            Os = info?.Os ?? inventory?.Os,
            Version = info?.Version ?? inventory?.AppVersion?.Split('+')[0],
            Capabilities = [.. inventory?.Capabilities ?? []],
            Labels = [.. inventory?.Labels ?? []],
            Providers = providers,
            Permissions = permissions,
            Sessions = listing.Sessions.Count,
            Working = listing.ListedAt is not null ? listing.Sessions.Count(static session => session.State == "working") : presence?.ActiveSessions,
            Paired = host.IsPaired,
            Channel = host.ChannelPath switch { FleetChannelPath.Relayed => "relayed", FleetChannelPath.Direct => "direct", _ => null },
            LastSeen = host.LastSeen,
            Problem = host.State == RemoteMachineState.Online ? null : host.Problem,
        };
    }

    // What the servers last said of the machines: the inventories they publish and their presence, the first server online first.
    private (IReadOnlyDictionary<string, FleetDirectoryMachine> Machines, IReadOnlyDictionary<string, FleetPresence> Presence) Directory()
    {
        var machines = new Dictionary<string, FleetDirectoryMachine>(StringComparer.Ordinal);
        var presence = new Dictionary<string, FleetPresence>(StringComparer.Ordinal);
        IReadOnlyList<FleetServerConnection> connections = Servers?.Connections ?? [];
        foreach (var connection in connections.OrderBy(static connection => connection.Connector?.State == FleetConnectorState.Online ? 0 : 1))
        {
            if (connection.Connector?.Directory is not { } directory) continue;
            foreach (var (id, machine) in directory.Machines) machines.TryAdd(id, machine);
            foreach (var (id, value) in directory.Presence) presence.TryAdd(id, value);
        }

        return (machines, presence);
    }

    private FleetServerConnection? Connection(string? server)
    {
        var connections = Servers?.Connections ?? [];
        return server is not null
            ? connections.FirstOrDefault(candidate => string.Equals(candidate.Link.Id, server, StringComparison.Ordinal)
                || string.Equals(candidate.Link.Url.TrimEnd('/'), server.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            : connections.FirstOrDefault(static candidate => candidate.Connector?.State == FleetConnectorState.Online) ?? connections.FirstOrDefault();
    }

    private bool IsThisComputer(string machine) => string.Equals(machine.Trim(), machineName, StringComparison.OrdinalIgnoreCase);

    // A machine by its id, or by its name when one machine only has it.
    private RemoteSessionHost? Resolve(string machine)
    {
        var name = machine.Trim();
        if (fleet.Find(name) is { } byId) return byId;
        var found = fleet.Hosts.Where(host => string.Equals(Name(host), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return found.Length == 1 ? found[0] : null;
    }

    private static string Name(RemoteSessionHost host) => host.Listing.Info?.Name ?? host.Machine.Name;

    private static AltaFleetMeasures Measures(StatisticsMeasuresDto measures)
        => new(measures.ActiveMs, measures.Runs, measures.RunsFailed, measures.Requests, measures.InputTokens, measures.OutputTokens, measures.ToolCalls, measures.ToolFailures,
            measures.CostMicro.ToDictionary(static cost => cost.Key, static cost => cost.Value / 1_000_000m, StringComparer.Ordinal));

    // What a failure of a call to a server is: the code of its refusal, or why it was not reached.
    private static (string Status, string? Message)? Failure(Exception exception) => exception switch
    {
        FleetServerException refused => (refused.Code, refused.Message),
        HttpRequestException or TaskCanceledException or TimeoutException => ("unreachable", exception.Message),
        KeyNotFoundException => ("no_server", null),
        FleetDeviceListException broken => (FleetDeviceListException.Code, broken.Message),
        _ => null,
    };
}
