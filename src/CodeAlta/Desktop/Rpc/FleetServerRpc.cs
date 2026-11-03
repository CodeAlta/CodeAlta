using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using CodeAlta.Fleet.Server;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The CodeAlta Servers of this instance (<see cref="DesktopFleetServers"/>), for Settings > Fleet > Server, the server
/// of a space, and the Fleet canvas: create a server with its setup code, join one with an enrollment token, the state
/// of each link, enrollment tokens, the devices to approve, and the directory of the organization. Keys, signatures and
/// connections stay in the host: the page sees addresses, names, states, the words of keys and the facts of sessions.
/// </summary>
[NeoRpcService("fleetServer", Version = 1)]
internal sealed partial class FleetServerService
{
    private const int MaximumText = 2048;
    private readonly DesktopFleetServers? _servers;
    private readonly string? _epoch;

    internal FleetServerService() { }

    /// <summary>Creates the service over the servers of the instance.</summary>
    /// <param name="servers">The servers.</param>
    /// <param name="epoch">The host epoch requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="servers"/> is null.</exception>
    internal FleetServerService(DesktopFleetServers servers, string epoch)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _servers = servers;
        _epoch = epoch;
    }

    /// <summary>Gets the spaces of the instance, to say which are linked to each server; null for none.</summary>
    internal SpaceCatalog? Spaces { get; init; }

    /// <summary>Gets whether a machine is paired with this computer, so that its sessions open over the direct channel; null for none.</summary>
    internal Func<string, bool>? IsPaired { get; init; }

    /// <summary>Gets whether this computer reaches a machine: paired with it, or through the relay of a server; null for none.</summary>
    internal Func<string, bool>? IsReachable { get; init; }

    /// <summary>Gets the machines paired with this computer, which the directory shows while no server is linked; null for none.</summary>
    internal DesktopFleet? Fleet { get; init; }

    /// <summary>The servers of this instance and how each stands.</summary>
    [NeoRpcMethod("status")]
    public async Task<FleetServerStatus> StatusAsync(FleetServerRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, [], null, null);
        var spaces = Spaces is null ? [] : await Spaces.LoadAsync(cancellationToken).ConfigureAwait(false);
        return new("ok", [.. _servers!.Connections.Select(connection => View(connection, spaces))], _servers.DeviceId, _servers.Words);
    }

    /// <summary>Sets a server up with its setup code: this desktop becomes the owner's controller device.</summary>
    [NeoRpcMethod("create")]
    public async Task<FleetServerResult> CreateAsync(FleetServerCreateRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!FleetServerAddress.TryParse(request!.Url, out var address)) return new("invalid_url", null, null);
        if (request.Code is not { Length: > 0 and <= 128 } code || request.OwnerName is not { Length: > 0 and <= 128 } owner || request.OrganizationName is { Length: > 128 })
            return new("invalid_request", null, null);
        return await ActAsync(async () =>
        {
            var link = await _servers!.CreateServerAsync(address, code, owner, string.IsNullOrWhiteSpace(request.OrganizationName) ? null : request.OrganizationName,
                cancellationToken).ConfigureAwait(false);
            return Result(link) with { RecoveryWords = await TryCreateRecoveryKeyAsync(link.Id, cancellationToken).ConfigureAwait(false) };
        }).ConfigureAwait(false);
    }

    // The owner's recovery key is made with the server, its words shown once; when it cannot be made now, the server's card offers it.
    private async Task<string?> TryCreateRecoveryKeyAsync(string linkId, CancellationToken cancellationToken)
    {
        try { return await _servers!.CreateRecoveryKeyAsync(linkId, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (Failure(exception) is not null) { return null; }
    }

    /// <summary>Signs this computer in with the recovery key of a person whose devices are all lost, and gives it their keys.</summary>
    [NeoRpcMethod("recover")]
    public async Task<FleetServerResult> RecoverAsync(FleetServerRecoverRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!FleetServerAddress.TryParse(request!.Url, out var address)) return new("invalid_url", null, null);
        if (request.Words is not { Length: > 0 and <= 1024 } words) return new("invalid_request", null, null);
        return await ActAsync(async () => Result(await _servers!.RecoverAsync(address, words, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }

    /// <summary>Makes the recovery key of this computer's person at a server: its words, shown once; one made before stops working.</summary>
    [NeoRpcMethod("createRecoveryKey")]
    public async Task<FleetRecoveryKeyResult> CreateRecoveryKeyAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return new("invalid_request", null, null);
        try
        {
            var words = await _servers!.CreateRecoveryKeyAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            ChangedLocally?.Invoke();
            return new("ok", words, null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, failure.Message);
        }
    }

    /// <summary>Joins a server with an enrollment token: the link waits for its approval.</summary>
    [NeoRpcMethod("join")]
    public async Task<FleetServerResult> JoinAsync(FleetServerJoinRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!FleetServerAddress.TryParse(request!.Url, out var address)) return new("invalid_url", null, null);
        if (request.Token is not { Length: > 0 and <= 256 } token) return new("invalid_request", null, null);
        return await ActAsync(async () => Result(await _servers!.JoinAsync(address, token.Trim(), cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }

    /// <summary>Leaves a server: the link goes, and the spaces linked to it are unlinked.</summary>
    [NeoRpcMethod("leave")]
    public async Task<FleetServerActionResult> LeaveAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return new("invalid_request", null);
        try
        {
            var (removed, withdrawn) = await _servers!.LeaveAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            // Left without the server's answer: what it holds stays there, which the page says.
            return new(!removed ? "not_found" : withdrawn is null ? "left_unreachable" : "ok", null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new("write_failed", exception.Message);
        }
    }

    /// <summary>Makes an enrollment token for a machine, shown once.</summary>
    [NeoRpcMethod("createToken")]
    public async Task<FleetServerTokenResult> CreateTokenAsync(FleetServerTokenRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId) || request.Hours is < 1 or > 24 || request.Label is { Length: > 128 }) return new("invalid_request", null, null, null);
        try
        {
            var token = await _servers!.CreateEnrollmentTokenAsync(request.LinkId, request.Hours is { } hours ? TimeSpan.FromHours(hours) : null,
                string.IsNullOrWhiteSpace(request.Label) ? null : request.Label, cancellationToken).ConfigureAwait(false);
            return new("ok", token.Token, token.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, null, failure.Message);
        }
    }

    /// <summary>The devices of the organization of a server, the pending ones first.</summary>
    [NeoRpcMethod("devices")]
    public async Task<FleetServerDevicesResult> DevicesAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, [], null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return new("invalid_request", [], null);
        try
        {
            var devices = await _servers!.ListDevicesAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            // The recovery key is a member of the list, not a device: the server's card says whether there is one.
            return new("ok", [.. devices.Where(static device => device.Kind != FleetDeviceKinds.Recovery).OrderBy(static device => device.Status == "pending" ? 0 : device.Status == "approved" ? 1 : 2)
                .ThenByDescending(static device => device.CreatedAt).Select(device => DeviceView(device))], null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, [], failure.Message);
        }
    }

    /// <summary>
    /// Approves a pending device once the person confirmed it shows the words of its key: this device signs the entry of the
    /// device list that adds that key, and the device gets the person's keys only then.
    /// </summary>
    [NeoRpcMethod("approve")]
    public Task<FleetServerDeviceResult> ApproveAsync(FleetServerDeviceRequest request, CancellationToken cancellationToken)
        => request?.Words is { Length: > 256 }
            ? Task.FromResult(new FleetServerDeviceResult("invalid_request", null, null))
            : ChangeDeviceAsync(request!, (link, device) => _servers!.ApproveAsync(link, device, request!.Words, cancellationToken));

    /// <summary>Revokes a device, or refuses a pending one.</summary>
    [NeoRpcMethod("revoke")]
    public Task<FleetServerDeviceResult> RevokeAsync(FleetServerDeviceRequest request, CancellationToken cancellationToken)
        => ChangeDeviceAsync(request, (link, device) => _servers!.RevokeAsync(link, device, cancellationToken));

    /// <summary>The telemetry of the fleet of a server, its headers masked; for the owner's computer.</summary>
    [NeoRpcMethod("telemetry")]
    public async Task<FleetTelemetryResult> TelemetryAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return new("invalid_request", null, null);
        try
        {
            return new("ok", TelemetryView(await _servers!.GetTelemetryAsync(request.LinkId, cancellationToken).ConfigureAwait(false)), null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, failure.Message);
        }
    }

    /// <summary>
    /// Sets the telemetry of the fleet of a server: this computer signs the setting, which the server applies and pushes to
    /// the machines. A header given without a value keeps the value held; the page never sees one.
    /// </summary>
    [NeoRpcMethod("setTelemetry")]
    public async Task<FleetTelemetryResult> SetTelemetryAsync(FleetTelemetryRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId) || request.Endpoint is { Length: > MaximumText } || request.Environment is { Length: > 64 }
            || request.Signals is not { Length: <= 3 } || request.Headers is not { Length: <= FleetTelemetrySettingsDocuments.MaxHeaders }
            || !FleetTelemetryProtocols.IsKnown(request.Protocol) || !FleetTelemetryRoutes.IsKnown(request.Route))
            return new("invalid_request", null, null);
        try
        {
            var held = await _servers!.GetTelemetryAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            var current = HeldHeaders(held);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var edit in request.Headers)
            {
                var name = edit?.Name?.Trim();
                if (string.IsNullOrEmpty(name) || headers.ContainsKey(name)) return new("invalid_request", null, $"'{name}' is not a header, or is there twice.");
                var value = edit!.Value ?? current.GetValueOrDefault(name);
                if (value is null) return new("invalid_request", null, $"The header '{name}' has no value.");
                headers[name] = value.Trim();
            }

            var server = request.Route == FleetTelemetryRoutes.Server;
            var change = new FleetTelemetryChange(request.Enabled, string.IsNullOrWhiteSpace(request.Endpoint) ? null : request.Endpoint.Trim().TrimEnd('/'), request.Protocol,
                request.Route, [.. request.Signals.Distinct(StringComparer.Ordinal)], server ? null : headers,
                string.IsNullOrWhiteSpace(request.Environment) ? null : request.Environment.Trim());
            var set = await _servers.SetTelemetryAsync(request.LinkId, change, server ? headers : null, cancellationToken).ConfigureAwait(false);
            ChangedLocally?.Invoke();
            return new("ok", TelemetryView(set), null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, failure.Message);
        }
    }

    /// <summary>The updates of the fleet of a server: the versions of the server and of each machine, the policy and the recent campaigns.</summary>
    [NeoRpcMethod("updates")]
    public async Task<FleetUpdatesResult> UpdatesAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId)) return new("invalid_request", null, null);
        try
        {
            var (updates, machines) = await _servers!.GetUpdatesAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            return new("ok", UpdatesView(updates, machines), null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, failure.Message);
        }
    }

    /// <summary>
    /// Starts an update campaign: this computer reads the feed (the latest version unless one is given, the hash of each
    /// runtime package) and signs the manifest; the server updates the nodes, then itself.
    /// </summary>
    [NeoRpcMethod("startUpdate")]
    public async Task<FleetUpdateCampaignResult> StartUpdateAsync(FleetUpdateStartRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId) || request.Version is { Length: > 64 } || request.Feed is { Length: > MaximumText } || request.TimeZone is { Length: > 64 }
            || request.Machines is { Length: > FleetUpdateDocuments.MaxMachines })
            return new("invalid_request", null, null);
        DateTimeOffset? notBefore = null;
        if (!string.IsNullOrWhiteSpace(request.NotBefore))
        {
            if (!DateTimeOffset.TryParse(request.NotBefore, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return new("invalid_request", null, null);
            notBefore = parsed.ToUniversalTime();
        }

        try
        {
            var change = new FleetUpdateCampaignChange(string.IsNullOrWhiteSpace(request.Version) ? null : request.Version.Trim(),
                string.IsNullOrWhiteSpace(request.Feed) ? null : request.Feed.Trim(), notBefore, string.IsNullOrWhiteSpace(request.TimeZone) ? null : request.TimeZone,
                request.Machines is { Length: > 0 } machines ? machines : null, request.Rollback);
            var campaign = await _servers!.StartUpdateAsync(request.LinkId, change, cancellationToken).ConfigureAwait(false);
            ChangedLocally?.Invoke();
            return new("ok", CampaignView(campaign), null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, failure.Message);
        }
    }

    /// <summary>Cancels an update campaign before the server's step.</summary>
    [NeoRpcMethod("cancelUpdate")]
    public async Task<FleetUpdateCampaignResult> CancelUpdateAsync(FleetUpdateCancelRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId) || request.CampaignId is not { Length: > 0 and <= 64 }) return new("invalid_request", null, null);
        try
        {
            var campaign = await _servers!.CancelUpdateAsync(request.LinkId, request.CampaignId, cancellationToken).ConfigureAwait(false);
            ChangedLocally?.Invoke();
            return new("ok", CampaignView(campaign), null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, failure.Message);
        }
    }

    /// <summary>Sets the update policy of the fleet: manual, pinned to a version, or automatic at a maintenance window.</summary>
    [NeoRpcMethod("setUpdatePolicy")]
    public async Task<FleetUpdateActionResult> SetUpdatePolicyAsync(FleetUpdatePolicyRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId) || !FleetUpdateModes.IsKnown(request.Mode) || request.PinnedVersion is { Length: > 64 }
            || request.Feed is { Length: > MaximumText } || request.TimeZone is { Length: > 64 } || request.Window is { Length: > 5 })
            return new("invalid_request", null);
        try
        {
            static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            await _servers!.SetUpdatePolicyAsync(request.LinkId, new FleetUpdatePolicyChange(request.Mode, request.Mode == FleetUpdateModes.Pinned ? Text(request.PinnedVersion) : null,
                request.Mode == FleetUpdateModes.Automatic ? Text(request.Feed) : null, request.Mode == FleetUpdateModes.Automatic ? Text(request.Window) : null,
                request.Mode == FleetUpdateModes.Automatic ? Text(request.TimeZone) : null), cancellationToken).ConfigureAwait(false);
            ChangedLocally?.Invoke();
            return new("ok", null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message);
        }
    }

    /// <summary>Links a space to a server, or unlinks it.</summary>
    [NeoRpcMethod("linkSpace")]
    public async Task<FleetServerActionResult> LinkSpaceAsync(FleetServerSpaceRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        if (request!.SpaceId is not { Length: > 0 and <= 128 } spaceId || request.LinkId is not null && !FleetServerLinkStore.IsValidId(request.LinkId))
            return new("invalid_request", null);
        try
        {
            var change = await _servers!.LinkSpaceAsync(spaceId, request.LinkId, cancellationToken).ConfigureAwait(false);
            return new(change.Status switch
            {
                SpaceChangeStatus.Ok => "ok",
                SpaceChangeStatus.NotFound => "not_found",
                SpaceChangeStatus.Conflict => "conflict",
                SpaceChangeStatus.Invalid => "invalid_request",
                _ => "write_failed",
            }, change.Message);
        }
        catch (KeyNotFoundException) { return new("not_found", null); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return new("write_failed", exception.Message); }
    }

    /// <summary>
    /// The directory of the organization of a server, as its control connection last heard it: its machines with their
    /// presence and the sessions they publish, with what this computer can open (its own, those of a paired machine).
    /// </summary>
    [NeoRpcMethod("directory")]
    public FleetDirectoryResult Directory(FleetServerDirectoryRequest request)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return FleetDirectoryResult.Refused(refused);
        var connections = _servers!.Connections;
        var connection = request!.LinkId is { } id ? connections.FirstOrDefault(candidate => candidate.Link.Id == id)
            : connections.FirstOrDefault(static candidate => candidate.Connector?.State == FleetConnectorState.Online) ?? connections.FirstOrDefault();
        if (connection is null)
        {
            // No server: the machines paired with this computer, as this computer reaches them itself.
            var paired = Fleet?.Hosts.Where(static host => host.IsPaired).Select(FleetPairedMachine.Of).ToArray() ?? [];
            return paired.Length > 0 ? DescribePaired(paired) : FleetDirectoryResult.Refused("no_server");
        }

        return Describe(connection, _servers.DeviceId, IsPaired ?? (static _ => false), IsReachable ?? IsPaired ?? (static _ => false));
    }

    /// <summary>
    /// The statistics of "My fleet": the sums the server keeps of what the machines of this computer's person published, by machine,
    /// project, provider, model, day or month, with the names of machines and the projects this computer can name.
    /// </summary>
    [NeoRpcMethod("statistics")]
    public async Task<FleetStatisticsResult> StatisticsAsync(FleetStatisticsRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request!.LinkId is { } given && !FleetServerLinkStore.IsValidId(given) || request.From is not { Length: 10 } || request.To is not { Length: 10 }
            || request.TimeZone is { Length: > 64 } || request.GroupBy is { Length: > 3 } || request.GroupBy?.Any(group => !StatisticsGroupBy.All.Contains(group)) == true)
        {
            return new("invalid_request", null, null);
        }

        var connections = _servers!.Connections;
        var connection = request.LinkId is { } id ? connections.FirstOrDefault(candidate => candidate.Link.Id == id)
            : connections.FirstOrDefault(static candidate => candidate.Connector?.State == FleetConnectorState.Online) ?? connections.FirstOrDefault();
        if (connection is null) return new("no_server", null, null);
        try
        {
            var (response, projects) = await _servers.QueryStatisticsAsync(connection.Link.Id,
                new StatisticsQueryRequest(request.From, request.To, request.TimeZone, request.GroupBy, "mine"), cancellationToken).ConfigureAwait(false);
            return new("ok", StatisticsView(connection.Link.Id, response, projects, _servers.DeviceId), null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, failure.Message);
        }
    }

    private static FleetStatisticsView StatisticsView(string linkId, StatisticsQueryResponse response, IReadOnlyDictionary<string, string> projects, string? thisDevice)
    {
        var names = response.Machines.ToDictionary(static machine => machine.Id, static machine => machine.Name, StringComparer.Ordinal);
        static FleetStatisticsMeasuresView Measures(StatisticsMeasuresDto measures) => new(measures.ActiveMs, measures.Runs, measures.RunsFailed, measures.Requests,
            measures.InputTokens, measures.OutputTokens, measures.CacheReadTokens, measures.CacheWriteTokens, measures.ReasoningTokens, measures.ToolCalls, measures.ToolFailures,
            [.. measures.CostMicro.Select(static cost => new FleetStatisticsCostView(cost.Key, cost.Value / 1_000_000m))]);
        return new FleetStatisticsView(linkId, response.From, response.To, response.TimeZone, [.. response.GroupBy], Measures(response.Totals),
            [.. response.Groups.Select(group => new FleetStatisticsGroupView(group.Machine, group.Machine is { } machine ? names.GetValueOrDefault(machine) : null,
                group.Project, group.Project is { Length: > 0 } project ? projects.GetValueOrDefault(project) : null, group.Provider, group.Model, group.Period, Measures(group.Measures)))],
            [.. response.Machines.Select(machine => new FleetStatisticsMachineView(machine.Id, machine.Name, machine.Kind, machine.Id == thisDevice,
                machine.LastPublishedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), (int)Math.Min(machine.Sessions, int.MaxValue)))],
            response.Truncated);
    }

    private event Action? ChangedLocally;

    /// <summary>Says each time a link, a connection or the directory changed: the page reads them again.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<FleetServerEvent> Watch(FleetServerRequest request, CancellationToken cancellationToken)
        => new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.FleetServerEvent);

    internal async IAsyncEnumerable<FleetServerEvent> WatchAsync(FleetServerRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is not null) yield break;
        // One pending event is enough: the page reads everything again.
        var changes = Channel.CreateBounded<FleetServerEvent>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        void OnChanged() => changes.Writer.TryWrite(new FleetServerEvent("changed"));
        _servers!.Changed += OnChanged;
        ChangedLocally += OnChanged;
        // A paired machine changing its state or its sessions changes the directory shown without a server.
        if (Fleet is { } fleet) fleet.Changed += OnChanged;
        try
        {
            await foreach (var value in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
        }
        finally
        {
            _servers.Changed -= OnChanged;
            ChangedLocally -= OnChanged;
            if (Fleet is { } paired) paired.Changed -= OnChanged;
        }
    }

    /// <summary>The directory of a connection as the page shows it.</summary>
    /// <param name="connection">The link and its connector.</param>
    /// <param name="thisDevice">The device id of this computer, whose sessions are local.</param>
    /// <param name="isPaired">Whether a machine is paired with this computer.</param>
    /// <returns>The directory.</returns>
    internal static FleetDirectoryResult Describe(FleetServerConnection connection, string? thisDevice, Func<string, bool> isPaired) => Describe(connection, thisDevice, isPaired, isPaired);

    /// <summary>The directory of a connection as the page shows it.</summary>
    /// <param name="connection">The link and its connector.</param>
    /// <param name="thisDevice">The device id of this computer, whose sessions are local.</param>
    /// <param name="isPaired">Whether a machine is paired with this computer.</param>
    /// <param name="isReachable">Whether this computer reaches a machine, paired or through the relay: its sessions open.</param>
    /// <returns>The directory.</returns>
    internal static FleetDirectoryResult Describe(FleetServerConnection connection, string? thisDevice, Func<string, bool> isPaired, Func<string, bool> isReachable)
    {
        var directory = connection.Connector?.Directory ?? FleetDirectoryView.Empty;
        var ids = directory.Machines.Keys.Union(directory.Presence.Keys).Union(directory.Sessions.Values.Select(static session => session.MachineId)).ToHashSet(StringComparer.Ordinal);
        var machines = ids.Select(id =>
        {
            directory.Machines.TryGetValue(id, out var machine);
            directory.Presence.TryGetValue(id, out var presence);
            var self = string.Equals(id, thisDevice, StringComparison.Ordinal);
            var inventory = machine?.Inventory;
            return new FleetMachineView(id, inventory?.Name ?? id, machine?.Kind ?? FleetDeviceKinds.Node, inventory?.Os, inventory?.AppVersion,
                presence?.Online ?? false, presence?.State, presence?.ActiveSessions, self, !self && isPaired(id), [.. inventory?.Providers ?? []],
                directory.Sessions.Values.Count(session => session.MachineId == id), !self && isReachable(id));
        }).OrderBy(static machine => machine.IsThisComputer ? 0 : machine.Online ? 1 : 2).ThenBy(static machine => machine.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var openable = machines.Where(static machine => machine.IsThisComputer || machine.IsReachable).Select(static machine => machine.Id).ToHashSet(StringComparer.Ordinal);
        // Titles travel sealed with the metadata key of their space: opened here, in the host, never by the server.
        var keys = connection.Connector?.MetadataKeys;
        var sessions = directory.Sessions.Values.OrderByDescending(static session => session.Fact.UpdatedAt).Take(500).Select(session =>
        {
            var fact = session.Fact;
            var counts = fact.Counts ?? new FleetSessionCounts();
            var title = keys is not null && keys.TryOpen(fact.SessionId, fact.SealedMetadata, out var metadata) ? metadata!.Title : null;
            return new FleetSessionView(fact.SessionId, session.MachineId, fact.State, fact.ProviderType, fact.Model, Time(fact.CreatedAt), Time(fact.UpdatedAt),
                Clamp(counts.Turns), Clamp(counts.InputTokens + counts.OutputTokens), openable.Contains(session.MachineId), title);
        }).ToArray();
        return new FleetDirectoryResult("ok", connection.Link.Id, connection.Link.Url, connection.Link.OrganizationName, State(connection), machines, sessions);
    }

    /// <summary>
    /// The machines paired with this computer as the page shows them while no server is linked (status <c>paired</c>):
    /// each with its state, its version and the sessions it lists, which open here.
    /// </summary>
    /// <param name="paired">The paired machines.</param>
    /// <returns>The directory.</returns>
    internal static FleetDirectoryResult DescribePaired(IEnumerable<FleetPairedMachine> paired)
    {
        var machines = new List<FleetMachineView>();
        var sessions = new List<FleetSessionView>();
        foreach (var machine in paired)
        {
            var info = machine.Listing.Info;
            var listed = machine.Listing.Sessions;
            machines.Add(new FleetMachineView(machine.Id, info?.Name ?? machine.Name, FleetDeviceKinds.Node, info?.Os, info?.Version, machine.State == RemoteMachineState.Online,
                RemoteSessionHost.Describe(machine.State), listed.Count(static session => session.State == "working"), false, true,
                [.. info?.Providers.Select(static provider => provider.Name) ?? []], listed.Count, true));
            sessions.AddRange(listed.Select(session => new FleetSessionView(session.Id, machine.Id, PairedSessionState(session.State), session.ProviderKey, session.ModelId,
                Time(session.CreatedAt), Time(session.UpdatedAt), 0, 0, true, string.IsNullOrWhiteSpace(session.Title) ? null : session.Title)));
        }

        return new FleetDirectoryResult("paired", null, null, null, null,
            [.. machines.OrderBy(static machine => machine.Online ? 0 : 1).ThenBy(static machine => machine.Name, StringComparer.CurrentCultureIgnoreCase)],
            [.. sessions.OrderByDescending(static session => session.UpdatedAt, StringComparer.Ordinal).Take(500)]);
    }

    // The state of a session a machine lists (sessions.list), as the directory of a server names it.
    private static string PairedSessionState(string state) => state switch
    {
        "working" => FleetSessionStates.Running,
        "waiting" => FleetSessionStates.RequiresAction,
        "archived" => FleetSessionStates.Archived,
        _ => FleetSessionStates.Idle,
    };

    internal static FleetServerView View(FleetServerConnection connection, IReadOnlyList<SpaceDescriptor> spaces)
    {
        var link = connection.Link;
        return new FleetServerView(link.Id, link.Url, link.OrganizationName, link.State, State(connection), connection.Connector?.LastError?.Message,
            link.IsController, [.. spaces.Where(space => link.IsServer(space.Settings.Fleet?.Server)).Select(static space => space.Id)],
            link.IsController ? DesktopFleetServers.HasRecoveryKey(connection) : null);
    }

    // The state of a link's connection, as the page names it.
    private static string State(FleetServerConnection connection) => connection.Link.State == FleetServerLinkStates.Revoked ? "revoked" : connection.Connector?.State switch
    {
        FleetConnectorState.Online => "online",
        FleetConnectorState.PendingApproval => "pending",
        FleetConnectorState.Offline => "offline",
        FleetConnectorState.Unauthenticated => "unauthenticated",
        FleetConnectorState.Revoked => "revoked",
        FleetConnectorState.Connecting => "connecting",
        _ => connection.Link.State == FleetServerLinkStates.Pending ? "pending" : "offline",
    };

    // The page reads 32-bit numbers: counts beyond them are shown as their most.
    private static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    private static string Time(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The updates of a fleet as the page shows them.</summary>
    /// <param name="updates">The updates as the server gives them.</param>
    /// <param name="machines">The machines of the organization.</param>
    /// <returns>The view.</returns>
    internal static FleetUpdatesView UpdatesView(UpdatesDto updates, IReadOnlyList<MachineDto> machines)
    {
        var policy = updates.Policy.Policy is { } signed && FleetUpdateDocuments.TryParsePolicy(signed.Payload, out var parsed, out _) ? parsed : null;
        var server = updates.Server;
        return new FleetUpdatesView(server.Running, server.Pending?.Version, server.LastOutcome is { } outcome ? $"{outcome.Outcome} {outcome.Version}" : null,
            new FleetUpdatePolicyView(policy?.Mode ?? FleetUpdateModes.Manual, policy?.PinnedVersion, policy?.Feed, policy?.Window, policy?.TimeZone,
                updates.Policy.LastCheckAt is { } checkedAt ? Time(checkedAt) : null, updates.Policy.LastCheckMessage),
            [.. updates.Campaigns.Select(CampaignView)],
            [.. machines.Where(static machine => machine.Kind == FleetDeviceKinds.Node && machine.Status == "approved").OrderBy(static machine => machine.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(static machine => new FleetUpdateMachineView(machine.DeviceId, machine.Name, machine.Inventory?.AppVersion?.Split('+')[0], machine.Online))]);
    }

    /// <summary>A campaign as the page shows it.</summary>
    /// <param name="campaign">The campaign as the server gives it.</param>
    /// <returns>The view.</returns>
    internal static FleetUpdateCampaignView CampaignView(UpdateCampaignDto campaign)
        => new(campaign.Id, Clamp(campaign.Sequence), campaign.State, campaign.TargetVersion, campaign.Feed, campaign.NotBefore is { } start ? Time(start) : null, campaign.TimeZone,
            campaign.Automatic, campaign.Rollback, campaign.ServerState, campaign.ServerMessage, Time(campaign.CreatedAt),
            [.. campaign.Machines.Select(static machine => new FleetUpdateCampaignMachineView(machine.DeviceId, machine.Name, machine.State, machine.Running, machine.Message,
                machine.Online))]);

    /// <summary>The telemetry setting as the page shows it: the values of its headers masked.</summary>
    /// <param name="settings">The setting as the server holds it.</param>
    /// <returns>The view; the defaults of a new setting when there is none.</returns>
    internal static FleetTelemetryView TelemetryView(TelemetrySettingsDto settings)
    {
        if (settings.Settings is not { } signed || !FleetTelemetrySettingsDocuments.TryParse(signed.Payload, out var parsed))
            return new FleetTelemetryView(false, null, FleetTelemetryProtocols.HttpProtobuf, FleetTelemetryRoutes.Direct, [FleetTelemetrySignals.Traces, FleetTelemetrySignals.Metrics],
                [], null, null);
        return new FleetTelemetryView(parsed.Enabled, parsed.Endpoint, parsed.Protocol, parsed.Route, [.. parsed.Signals],
            [.. HeldHeaders(settings).OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(static pair => new FleetTelemetryHeaderView(pair.Key, Mask(pair.Value)))],
            parsed.Environment, settings.UpdatedAt is { } updated ? Time(updated) : null);
    }

    // The headers the setting uses: those of a direct route are signed in it, those of the server route only the server holds.
    private static Dictionary<string, string> HeldHeaders(TelemetrySettingsDto settings)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (settings.Settings is { } signed && FleetTelemetrySettingsDocuments.TryParse(signed.Payload, out var parsed))
        {
            var held = parsed.Route == FleetTelemetryRoutes.Server ? settings.ServerHeaders : parsed.Headers;
            foreach (var (name, value) in held ?? new Dictionary<string, string>()) headers[name] = value;
        }

        return headers;
    }

    // A secret is never shown: its last characters only, when it is long enough for them to tell nothing.
    private static string Mask(string value) => value.Length >= 16 ? "\u2022\u2022\u2022\u2022" + value[^4..] : "\u2022\u2022\u2022\u2022";

    internal static FleetDeviceView DeviceView(DeviceDto device)
    {
        string words;
        try { words = DeviceFingerprint.FromSubjectPublicKeyInfo(Convert.FromBase64String(device.PublicKey)).ToShortWords(); }
        catch (FormatException) { words = string.Empty; }
        return new FleetDeviceView(device.Id, device.Name, device.Kind, device.Status, device.Online, words, Time(device.CreatedAt));
    }

    private async Task<FleetServerDeviceResult> ChangeDeviceAsync(FleetServerDeviceRequest request, Func<string, string, Task<DeviceDto>> change)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (!FleetServerLinkStore.IsValidId(request!.LinkId) || request.DeviceId is not { Length: > 0 and <= 64 }) return new("invalid_request", null, null);
        try
        {
            var device = await change(request.LinkId, request.DeviceId).ConfigureAwait(false);
            ChangedLocally?.Invoke();
            return new("ok", DeviceView(device), null);
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, null, failure.Message);
        }
    }

    private async Task<FleetServerResult> ActAsync(Func<Task<FleetServerResult>> act)
    {
        try
        {
            var result = await act().ConfigureAwait(false);
            ChangedLocally?.Invoke();
            return result;
        }
        catch (InvalidOperationException exception) { return new("already_linked", null, exception.Message); }
        catch (Exception exception) when (Failure(exception) is { } failure) { return new(failure.Status, null, failure.Message); }
    }

    private FleetServerResult Result(FleetServerLink link) => new("ok", View(new FleetServerConnection(link, null), []), null);

    // What a failure of a call to a server is, for the page: the code of the server's refusal, or why it was not reached.
    private static (string Status, string? Message)? Failure(Exception exception) => exception switch
    {
        FleetServerException refused => (refused.Code, refused.Message),
        CodeAlta.Fleet.Update.UpdateException update => (update.Code, update.Message),
        FleetDeviceListException broken => (FleetDeviceListException.Code, broken.Message),
        FleetRecoveryException recovery => ("recovery.refused", recovery.Message),
        HttpRequestException or TaskCanceledException or TimeoutException => ("unreachable", exception.Message),
        KeyNotFoundException => ("not_found", null),
        IOException or UnauthorizedAccessException or InvalidDataException => ("write_failed", exception.Message),
        ArgumentException => ("invalid_request", exception.Message),
        _ => null,
    };

    private string? Refuse(string? expectedEpoch)
        => _servers is null ? "unavailable" : !string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? "stale_epoch" : null;
}

/// <summary>A request about the servers, from a page of a host lifetime.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
internal sealed record FleetServerRequest(string ExpectedEpoch);

/// <summary>A server this instance is linked to, as the page shows it.</summary>
/// <param name="Id">The id of the link.</param>
/// <param name="Url">The address of the server.</param>
/// <param name="Organization">The name of its organization, once known.</param>
/// <param name="Link">How this device stands there: <c>pending</c>, <c>approved</c>, <c>revoked</c>.</param>
/// <param name="Connection">Its control connection: <c>online</c>, <c>connecting</c>, <c>offline</c>, <c>pending</c>, <c>unauthenticated</c>, <c>revoked</c>.</param>
/// <param name="Problem">What the last failure said; null when there was none.</param>
/// <param name="Controller">Whether this device may make tokens and approve devices there.</param>
/// <param name="Spaces">The spaces that publish to it.</param>
/// <param name="RecoveryKey">Whether the person of this computer has a recovery key there, for an owner's computer; null when unknown or not asked.</param>
internal sealed record FleetServerView(string Id, string Url, string? Organization, string Link, string Connection, string? Problem, bool Controller, string[] Spaces,
    bool? RecoveryKey = null);

/// <summary>The servers of this instance.</summary>
/// <param name="Status"><c>ok</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Servers">The servers, in the order they were linked.</param>
/// <param name="DeviceId">The device id of this computer, once its key exists.</param>
/// <param name="Words">The words of its key.</param>
internal sealed record FleetServerStatus(string Status, FleetServerView[] Servers, string? DeviceId, string? Words);

/// <summary>A server to set up with its setup code.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Url">The address of the server.</param>
/// <param name="Code">The setup code <c>altaserver setup-code</c> prints.</param>
/// <param name="OwnerName">The name of the owner.</param>
/// <param name="OrganizationName">The name of the organization; null for the owner's.</param>
internal sealed record FleetServerCreateRequest(string ExpectedEpoch, string Url, string Code, string OwnerName, string? OrganizationName = null);

/// <summary>A server to join with an enrollment token.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Url">The address of the server.</param>
/// <param name="Token">The enrollment token.</param>
internal sealed record FleetServerJoinRequest(string ExpectedEpoch, string Url, string Token);

/// <summary>The outcome of a setup or a join.</summary>
/// <param name="Status">
/// <c>ok</c>, <c>invalid_url</c>, <c>invalid_request</c>, <c>already_linked</c>, <c>unreachable</c>, the code of the server's refusal
/// (<c>setup.invalid_code</c>, <c>setup.code_expired</c>, <c>setup.loopback_only</c>, <c>setup.done</c>, <c>enrollment.*</c>), <c>stale_epoch</c>
/// or <c>unavailable</c>.
/// </param>
/// <param name="Server">The server, once linked.</param>
/// <param name="Message">What the failure said.</param>
/// <param name="RecoveryWords">The words of the recovery key made with a new server, shown once; null when none was made.</param>
internal sealed record FleetServerResult(string Status, FleetServerView? Server, string? Message, string? RecoveryWords = null);

/// <summary>A server to recover with the recovery key of a person whose devices are all lost.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Url">The address of the server.</param>
/// <param name="Words">The words of the recovery key.</param>
internal sealed record FleetServerRecoverRequest(string ExpectedEpoch, string Url, string Words);

/// <summary>A new recovery key, its words shown once.</summary>
/// <param name="Status"><c>ok</c>, the code of the server's refusal, <c>device_list.broken</c>, <c>unreachable</c>, <c>not_found</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Words">The 32 words of the recovery key.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetRecoveryKeyResult(string Status, string? Words, string? Message);

/// <summary>A request about one link.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The id of the link.</param>
internal sealed record FleetServerLinkRequest(string ExpectedEpoch, string LinkId);

/// <summary>A request for the directory of a server.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The id of the link; null for the first server online.</param>
internal sealed record FleetServerDirectoryRequest(string ExpectedEpoch, string? LinkId = null);

/// <summary>The outcome of an action: <c>ok</c>, <c>not_found</c>, <c>invalid_request</c>, <c>write_failed</c>, <c>conflict</c>, <c>stale_epoch</c> or <c>unavailable</c>.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetServerActionResult(string Status, string? Message);

/// <summary>A request for an enrollment token.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The id of the link.</param>
/// <param name="Hours">How many hours it works, 1 to 24; null for one.</param>
/// <param name="Label">What it is for.</param>
internal sealed record FleetServerTokenRequest(string ExpectedEpoch, string LinkId, int? Hours = null, string? Label = null);

/// <summary>A new enrollment token, shown once.</summary>
/// <param name="Status"><c>ok</c>, the code of the server's refusal, <c>unreachable</c>, <c>not_found</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Token">The token.</param>
/// <param name="ExpiresAt">When it stops working.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetServerTokenResult(string Status, string? Token, string? ExpiresAt, string? Message);

/// <summary>A device of an organization.</summary>
/// <param name="Id">Its device id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind"><c>controller</c> or <c>node</c>.</param>
/// <param name="Status"><c>pending</c>, <c>approved</c> or <c>revoked</c>.</param>
/// <param name="Online">Whether it is connected.</param>
/// <param name="Words">The words of its key, which the device shows too.</param>
/// <param name="CreatedAt">When it registered.</param>
internal sealed record FleetDeviceView(string Id, string Name, string Kind, string Status, bool Online, string Words, string CreatedAt);

/// <summary>The devices of an organization.</summary>
/// <param name="Status"><c>ok</c>, the code of the server's refusal, <c>unreachable</c>, <c>not_found</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Devices">The devices, the pending ones first.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetServerDevicesResult(string Status, FleetDeviceView[] Devices, string? Message);

/// <summary>A request about a device of a server.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The id of the link.</param>
/// <param name="DeviceId">The device.</param>
/// <param name="Words">For an approval, the words of its key the person compared with the device's screen; null when none were.</param>
internal sealed record FleetServerDeviceRequest(string ExpectedEpoch, string LinkId, string DeviceId, string? Words = null);

/// <summary>The outcome of an approval or a revocation.</summary>
/// <param name="Status"><c>ok</c>, the code of the server's refusal, <c>unreachable</c>, <c>not_found</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Device">The device after the change.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetServerDeviceResult(string Status, FleetDeviceView? Device, string? Message);

/// <summary>A space to link to a server, or to unlink.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="SpaceId">The space.</param>
/// <param name="LinkId">The link of the server; null unlinks the space.</param>
internal sealed record FleetServerSpaceRequest(string ExpectedEpoch, string SpaceId, string? LinkId);

/// <summary>A machine paired with this computer, as the directory reads it while no server is linked.</summary>
/// <param name="Id">Its device id.</param>
/// <param name="Name">The name it was paired with.</param>
/// <param name="State">Its connection state.</param>
/// <param name="Listing">What this computer last read of it.</param>
internal sealed record FleetPairedMachine(string Id, string Name, RemoteMachineState State, RemoteListing Listing)
{
    /// <summary>Reads a paired machine from its host.</summary>
    /// <param name="host">The host of the machine.</param>
    /// <returns>The machine.</returns>
    internal static FleetPairedMachine Of(RemoteSessionHost host) => new(host.Machine.DeviceId, host.Machine.Name, host.State, host.Listing);
}

/// <summary>A machine of the directory.</summary>
/// <param name="Id">Its device id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind"><c>controller</c> or <c>node</c>.</param>
/// <param name="Os">Its system.</param>
/// <param name="Version">The version of CodeAlta it runs.</param>
/// <param name="Online">Whether it is connected to the server.</param>
/// <param name="Presence">What it last said of itself: <c>active</c>, <c>busy</c>.</param>
/// <param name="ActiveSessions">How many of its published sessions run.</param>
/// <param name="IsThisComputer">Whether it is this computer.</param>
/// <param name="IsPaired">Whether it is paired with this computer: its sessions open over the direct channel.</param>
/// <param name="Providers">The types of its ready providers.</param>
/// <param name="Sessions">How many sessions it publishes.</param>
/// <param name="IsReachable">Whether this computer reaches it, paired or through the relay of the server: its sessions open here.</param>
internal sealed record FleetMachineView(string Id, string Name, string Kind, string? Os, string? Version, bool Online, string? Presence, int? ActiveSessions,
    bool IsThisComputer, bool IsPaired, string[] Providers, int Sessions, bool IsReachable);

/// <summary>A published session of the directory: its facts, and its title once opened on this computer.</summary>
/// <param name="Id">The session.</param>
/// <param name="MachineId">The machine that owns it.</param>
/// <param name="State"><c>running</c>, <c>requires_action</c>, <c>idle</c> or <c>archived</c>.</param>
/// <param name="ProviderType">The type of its provider.</param>
/// <param name="Model">Its model.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="Turns">Its turns, when published.</param>
/// <param name="Tokens">Its input and output tokens, when published.</param>
/// <param name="CanOpen">Whether this computer can open it: its own, or one of a paired machine.</param>
/// <param name="Title">Its title, opened with the metadata key of the person; null when it has none or this computer cannot open it.</param>
internal sealed record FleetSessionView(string Id, string MachineId, string State, string? ProviderType, string? Model, string CreatedAt, string UpdatedAt,
    int Turns, int Tokens, bool CanOpen, string? Title = null);

/// <summary>The directory of a server.</summary>
/// <param name="Status">
/// <c>ok</c>, <c>paired</c> (no server: the machines paired with this computer), <c>no_server</c>, <c>stale_epoch</c> or <c>unavailable</c>.
/// </param>
/// <param name="LinkId">The link.</param>
/// <param name="Url">The address of the server.</param>
/// <param name="Organization">The name of its organization.</param>
/// <param name="Connection">The state of the control connection (<see cref="FleetServerView.Connection"/>).</param>
/// <param name="Machines">The machines: this computer first, then those online.</param>
/// <param name="Sessions">The published sessions, the latest first.</param>
internal sealed record FleetDirectoryResult(string Status, string? LinkId, string? Url, string? Organization, string? Connection, FleetMachineView[] Machines,
    FleetSessionView[] Sessions)
{
    internal static FleetDirectoryResult Refused(string status) => new(status, null, null, null, null, [], []);
}

/// <summary>A change of the servers.</summary>
/// <param name="Kind"><c>changed</c>.</param>
internal sealed record FleetServerEvent(string Kind);

/// <summary>A machine of the fleet and the version it runs.</summary>
/// <param name="Id">Its device.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">The version of CodeAlta it last said it runs, or null.</param>
/// <param name="Online">Whether it is connected to the server.</param>
internal sealed record FleetUpdateMachineView(string Id, string Name, string? Version, bool Online);

/// <summary>The update policy of a fleet as the page shows it.</summary>
/// <param name="Mode"><c>manual</c>, <c>pinned</c> or <c>automatic</c>.</param>
/// <param name="PinnedVersion">The version of a pinned fleet.</param>
/// <param name="Feed">The feed an automatic fleet follows; null for nuget.org.</param>
/// <param name="Window">The start of the maintenance window, HH:mm.</param>
/// <param name="TimeZone">The time zone of the window.</param>
/// <param name="LastCheckAt">When an automatic policy last read its feed.</param>
/// <param name="LastCheckMessage">What it found.</param>
internal sealed record FleetUpdatePolicyView(string Mode, string? PinnedVersion, string? Feed, string? Window, string? TimeZone, string? LastCheckAt, string? LastCheckMessage);

/// <summary>A machine in a campaign as the page shows it.</summary>
/// <param name="Id">Its device.</param>
/// <param name="Name">Its name.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Running">The version it runs, when it said so.</param>
/// <param name="Message">What happened, for a failure or a refusal.</param>
/// <param name="Online">Whether it is connected.</param>
internal sealed record FleetUpdateCampaignMachineView(string Id, string Name, string State, string? Running, string? Message, bool Online);

/// <summary>An update campaign as the page shows it.</summary>
/// <param name="Id">The campaign.</param>
/// <param name="Sequence">Its number.</param>
/// <param name="State">Its state.</param>
/// <param name="TargetVersion">The version it installs.</param>
/// <param name="Feed">The feed.</param>
/// <param name="NotBefore">When it starts (UTC, ISO 8601), or null for at once.</param>
/// <param name="TimeZone">The time zone the time was chosen in.</param>
/// <param name="Automatic">Whether the automatic policy started it.</param>
/// <param name="Rollback">Whether a person chose an older version.</param>
/// <param name="ServerState">Where the server's own update stands.</param>
/// <param name="ServerMessage">What happened to it.</param>
/// <param name="CreatedAt">When it was started.</param>
/// <param name="Machines">Its machines.</param>
internal sealed record FleetUpdateCampaignView(string Id, int Sequence, string State, string TargetVersion, string Feed, string? NotBefore, string? TimeZone, bool Automatic,
    bool Rollback, string ServerState, string? ServerMessage, string CreatedAt, FleetUpdateCampaignMachineView[] Machines);

/// <summary>The updates of a fleet as the page shows them.</summary>
/// <param name="ServerVersion">The version the server runs.</param>
/// <param name="ServerPending">The version the server switches to, or null.</param>
/// <param name="ServerLastOutcome">How its last switch ended (outcome and version), or null.</param>
/// <param name="Policy">The update policy.</param>
/// <param name="Campaigns">The recent campaigns, the newest first.</param>
/// <param name="Machines">The nodes and their versions.</param>
internal sealed record FleetUpdatesView(string ServerVersion, string? ServerPending, string? ServerLastOutcome, FleetUpdatePolicyView Policy, FleetUpdateCampaignView[] Campaigns,
    FleetUpdateMachineView[] Machines);

/// <summary>The updates of a fleet, or why they could not be read.</summary>
/// <param name="Status"><c>ok</c>, the code of the server's refusal, <c>unreachable</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Updates">The updates.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetUpdatesResult(string Status, FleetUpdatesView? Updates, string? Message);

/// <summary>A campaign started or cancelled, or why not.</summary>
/// <param name="Status"><c>ok</c>, or the code of the refusal.</param>
/// <param name="Campaign">The campaign.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetUpdateCampaignResult(string Status, FleetUpdateCampaignView? Campaign, string? Message);

/// <summary>Whether a change of the updates was made.</summary>
/// <param name="Status"><c>ok</c>, or the code of the refusal.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetUpdateActionResult(string Status, string? Message);

/// <summary>An update campaign to start.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link of the server.</param>
/// <param name="Version">The version; null or empty for the latest.</param>
/// <param name="Feed">The feed; null or empty for nuget.org.</param>
/// <param name="NotBefore">When it starts, ISO 8601 with its offset; null for now.</param>
/// <param name="TimeZone">The time zone the person chose the time in.</param>
/// <param name="Machines">The device ids of the nodes; null or empty for all.</param>
/// <param name="Rollback">Whether the person chose an older version.</param>
internal sealed record FleetUpdateStartRequest(string ExpectedEpoch, string LinkId, string? Version, string? Feed, string? NotBefore, string? TimeZone, string[]? Machines,
    bool Rollback);

/// <summary>A campaign to cancel.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link of the server.</param>
/// <param name="CampaignId">The campaign.</param>
internal sealed record FleetUpdateCancelRequest(string ExpectedEpoch, string LinkId, string CampaignId);

/// <summary>An update policy to set.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link of the server.</param>
/// <param name="Mode"><c>manual</c>, <c>pinned</c> or <c>automatic</c>.</param>
/// <param name="PinnedVersion">The version of a pinned fleet.</param>
/// <param name="Feed">The feed an automatic fleet follows.</param>
/// <param name="Window">The start of its maintenance window, HH:mm.</param>
/// <param name="TimeZone">The time zone of the window.</param>
internal sealed record FleetUpdatePolicyRequest(string ExpectedEpoch, string LinkId, string Mode, string? PinnedVersion, string? Feed, string? Window, string? TimeZone);

/// <summary>A header of the collector, its value masked.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Masked">Its value as the page shows it.</param>
internal sealed record FleetTelemetryHeaderView(string Name, string Masked);

/// <summary>The telemetry of a fleet as the page shows it.</summary>
/// <param name="Enabled">Whether the machines send.</param>
/// <param name="Endpoint">The address of the collector.</param>
/// <param name="Protocol"><c>http/protobuf</c> or <c>grpc</c>.</param>
/// <param name="Route"><c>direct</c> (each machine sends to the collector) or <c>server</c> (through the server).</param>
/// <param name="Signals"><c>traces</c>, <c>metrics</c>, <c>logs</c>.</param>
/// <param name="Headers">The headers sent to the collector, masked.</param>
/// <param name="Environment">The environment the machines name, or null.</param>
/// <param name="UpdatedAt">When it was last set, or null.</param>
internal sealed record FleetTelemetryView(bool Enabled, string? Endpoint, string Protocol, string Route, string[] Signals, FleetTelemetryHeaderView[] Headers,
    string? Environment, string? UpdatedAt);

/// <summary>The telemetry of a fleet, or why it could not be read or set.</summary>
/// <param name="Status"><c>ok</c>, the code of the server's refusal, <c>unreachable</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Telemetry">The setting.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetTelemetryResult(string Status, FleetTelemetryView? Telemetry, string? Message);

/// <summary>A header of the collector as the page edits it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Value">Its new value; null keeps the one held.</param>
internal sealed record FleetTelemetryHeaderEdit(string Name, string? Value);

/// <summary>The telemetry of a fleet to set.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link of the server.</param>
/// <param name="Enabled">Whether the machines send.</param>
/// <param name="Endpoint">The address of the collector.</param>
/// <param name="Protocol"><c>http/protobuf</c> or <c>grpc</c>.</param>
/// <param name="Route"><c>direct</c> or <c>server</c>.</param>
/// <param name="Signals">The signals.</param>
/// <param name="Headers">The headers; one without a value keeps the value held.</param>
/// <param name="Environment">The environment the machines name, or null.</param>
internal sealed record FleetTelemetryRequest(string ExpectedEpoch, string LinkId, bool Enabled, string? Endpoint, string Protocol, string Route, string[] Signals,
    FleetTelemetryHeaderEdit[] Headers, string? Environment);

/// <summary>A question about the statistics of "My fleet".</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link of the server; null for the one online.</param>
/// <param name="From">The first local day, <c>yyyy-MM-dd</c>.</param>
/// <param name="To">The last local day, <c>yyyy-MM-dd</c>.</param>
/// <param name="TimeZone">The time zone of the days; null for UTC.</param>
/// <param name="GroupBy">The dimensions: <c>machine</c>, <c>project</c>, <c>provider</c>, <c>model</c>, <c>day</c>, <c>month</c>; at most three.</param>
internal sealed record FleetStatisticsRequest(string ExpectedEpoch, string? LinkId, string From, string To, string? TimeZone, string[]? GroupBy);

/// <summary>An estimated cost.</summary>
/// <param name="Unit">The unit, such as <c>usd</c>.</param>
/// <param name="Amount">The amount, in the unit.</param>
internal sealed record FleetStatisticsCostView(string Unit, decimal Amount);

/// <summary>The sums of a group of the statistics of a fleet.</summary>
/// <param name="ActiveMs">The active time of the runs.</param>
/// <param name="Runs">The runs.</param>
/// <param name="RunsFailed">The runs that failed.</param>
/// <param name="Requests">The requests to models.</param>
/// <param name="InputTokens">The input tokens.</param>
/// <param name="OutputTokens">The output tokens.</param>
/// <param name="CacheReadTokens">The input tokens read from a cache.</param>
/// <param name="CacheWriteTokens">The input tokens written to a cache.</param>
/// <param name="ReasoningTokens">The reasoning tokens.</param>
/// <param name="ToolCalls">The tool calls.</param>
/// <param name="ToolFailures">The tool calls that failed.</param>
/// <param name="Costs">The estimated costs, by unit.</param>
internal sealed record FleetStatisticsMeasuresView(decimal ActiveMs, decimal Runs, decimal RunsFailed, decimal Requests, decimal InputTokens, decimal OutputTokens,
    decimal CacheReadTokens, decimal CacheWriteTokens, decimal ReasoningTokens, decimal ToolCalls, decimal ToolFailures, FleetStatisticsCostView[] Costs);

/// <summary>A group of the statistics of a fleet.</summary>
/// <param name="Machine">The device id of the machine, when grouped by machine.</param>
/// <param name="MachineName">Its name.</param>
/// <param name="Project">The key of the project, when grouped by project; empty for the sessions without a project.</param>
/// <param name="ProjectName">Its name, when this computer has the project; null otherwise.</param>
/// <param name="Provider">The provider, when grouped by provider.</param>
/// <param name="Model">The model, when grouped by model.</param>
/// <param name="Period">The day or the month, when grouped by one.</param>
/// <param name="Measures">The sums.</param>
internal sealed record FleetStatisticsGroupView(string? Machine, string? MachineName, string? Project, string? ProjectName, string? Provider, string? Model, string? Period,
    FleetStatisticsMeasuresView Measures);

/// <summary>A machine of "My fleet" and its last publication of statistics.</summary>
/// <param name="Id">The device id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind"><c>node</c> or a desktop's kind.</param>
/// <param name="IsThisComputer">Whether it is this computer.</param>
/// <param name="LastPublishedAt">When it last published statistics, ISO 8601; null when never.</param>
/// <param name="Sessions">The sessions it published statistics of.</param>
internal sealed record FleetStatisticsMachineView(string Id, string Name, string Kind, bool IsThisComputer, string? LastPublishedAt, int Sessions);

/// <summary>The statistics of "My fleet".</summary>
/// <param name="LinkId">The link of the server.</param>
/// <param name="From">The first local day.</param>
/// <param name="To">The last local day.</param>
/// <param name="TimeZone">The time zone of the days.</param>
/// <param name="GroupBy">The dimensions of the groups.</param>
/// <param name="Totals">The sums of the period.</param>
/// <param name="Groups">The groups, the largest first by tokens.</param>
/// <param name="Machines">The machines of the person.</param>
/// <param name="Truncated">Whether groups were left out.</param>
internal sealed record FleetStatisticsView(string LinkId, string From, string To, string TimeZone, string[] GroupBy, FleetStatisticsMeasuresView Totals,
    FleetStatisticsGroupView[] Groups, FleetStatisticsMachineView[] Machines, bool Truncated);

/// <summary>The answer of <c>statistics</c>.</summary>
/// <param name="Status"><c>ok</c>, <c>no_server</c>, <c>unreachable</c>, <c>statistics.invalid</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="View">The statistics, when <c>ok</c>.</param>
/// <param name="Message">What went wrong, or null.</param>
internal sealed record FleetStatisticsResult(string Status, FleetStatisticsView? View, string? Message);
