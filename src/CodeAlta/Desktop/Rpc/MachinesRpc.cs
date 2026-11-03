using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The machines this instance paired with (<see cref="DesktopFleet"/>): their list and state, pairing from a link the
/// person pastes, forgetting one, and a new session on one. Keys, links and connections stay in the host: the page sees
/// names, states, addresses and the words of keys.
/// </summary>
[NeoRpcService("machines", Version = 1)]
internal sealed class MachinesService
{
    private const int MaximumLinkLength = 4096;
    private const int MaximumPromptLength = 32768;
    private readonly DesktopFleet? _fleet;
    private readonly string? _epoch;

    internal MachinesService() { }

    /// <summary>Creates the service over the fleet of the instance.</summary>
    /// <param name="fleet">The paired machines.</param>
    /// <param name="epoch">The host epoch requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fleet"/> is null.</exception>
    internal MachinesService(DesktopFleet fleet, string epoch)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _fleet = fleet;
        _epoch = epoch;
    }

    /// <summary>Lists the paired machines and their state.</summary>
    [NeoRpcMethod("list")]
    public MachinesList List(MachinesRequest request)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, _epoch, []);
        return new("ok", _epoch, [.. _fleet!.Hosts.Select(View)]);
    }

    /// <summary>Reads a pairing link and says what it names, without connecting.</summary>
    [NeoRpcMethod("previewPairing")]
    public MachinePairPreview PreviewPairing(MachinePairLinkRequest request)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachinePairPreview.Refused(refused);
        if (request!.Link is not { Length: > 0 and <= MaximumLinkLength } link) return MachinePairPreview.Refused("invalid_link");
        if (!_fleet!.TryPreview(link, out var preview, out var error)) return MachinePairPreview.Refused(error == PairingPayloadError.Expired ? "expired" : "invalid_link");
        return new("ok", preview.Name, preview.DeviceId, preview.Words, [.. preview.Addresses],
            preview.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), preview.AlreadyPaired);
    }

    /// <summary>Pairs with the machine of a link.</summary>
    [NeoRpcMethod("pair")]
    public async Task<MachinePairResult> PairAsync(MachinePairLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request!.Link is not { Length: > 0 and <= MaximumLinkLength } link) return new("invalid_link", null, null);
        try
        {
            var machine = await _fleet!.PairAsync(link, cancellationToken).ConfigureAwait(false);
            return new("ok", _fleet.Find(machine.DeviceId) is { } host ? View(host) : null, null);
        }
        catch (FormatException) { return new(_fleet!.TryPreview(link, out _, out var error) || error != PairingPayloadError.Expired ? "invalid_link" : "expired", null, null); }
        catch (DeviceAuthenticationException exception) { return new("refused", null, exception.Message); }
        catch (FleetException exception) when (exception.Error.Code == FleetNodeErrorCodes.PairingRefused) { return new("link_refused", null, null); }
        catch (FleetException exception) { return new("refused", null, exception.Error.Message); }
        catch (Exception exception) when (exception is IOException or TimeoutException or System.Security.Authentication.AuthenticationException)
        {
            return new("unreachable", null, exception.Message);
        }
    }

    /// <summary>Forgets a paired machine.</summary>
    [NeoRpcMethod("forget")]
    public async Task<MachineActionResult> ForgetAsync(MachineRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id) return new("invalid_request");
        try
        {
            return new(await _fleet!.ForgetAsync(id, cancellationToken).ConfigureAwait(false) ? "ok" : "not_found");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException or InvalidDataException)
        {
            return new("write_failed");
        }
    }

    /// <summary>Asks a machine for its list now, and gives the machines as they are then.</summary>
    [NeoRpcMethod("refresh")]
    public MachinesList Refresh(MachinesRequest request)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, _epoch, []);
        foreach (var host in _fleet!.Hosts) host.RequestRefresh();
        return List(request!);
    }

    /// <summary>Creates a session on a machine, in one of its projects, with its first prompt.</summary>
    [NeoRpcMethod("newSession")]
    public async Task<MachineNewSessionResult> NewSessionAsync(MachineNewSessionRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || request.ClientRequestId is not { Length: > 0 and <= 256 } key
            || request.ProjectId is { Length: > 256 } || request.Prompt is { Length: > MaximumPromptLength }) return new("invalid_request", null, null);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, null);
        try
        {
            var created = await host.CreateRemoteSessionAsync(new FleetCreateSessionParams(request.ProjectId, null, null,
                string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt), key, cancellationToken).ConfigureAwait(false);
            return new(created.Send is { Status: not ("accepted" or "replay") } send ? send.Status : "ok", created.SessionId, null);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null, null); }
        catch (FleetException exception) { return new(exception.Error.Code, null, exception.Error.Message); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, null, exception.Message); }
    }

    /// <summary>Says each time the machines changed (paired, forgotten, a state, a list of sessions): the page reads them again.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<MachinesEvent> Watch(MachinesRequest request, CancellationToken cancellationToken)
        => new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.MachinesEvent);

    internal async IAsyncEnumerable<MachinesEvent> WatchAsync(MachinesRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is not null) yield break;
        // One pending event is enough: the page reads the whole list.
        var changes = Channel.CreateBounded<MachinesEvent>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        void OnChanged() => changes.Writer.TryWrite(new MachinesEvent("changed"));
        _fleet!.Changed += OnChanged;
        try
        {
            await foreach (var value in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
        }
        finally
        {
            _fleet.Changed -= OnChanged;
        }
    }

    private string? Refuse(string? expectedEpoch)
        => _fleet is null ? "unavailable" : !string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? "stale_epoch" : null;

    internal static MachineView View(RemoteSessionHost host)
    {
        var machine = host.Machine;
        var listing = host.Listing;
        var display = machine.Fingerprint.ToDisplayString();
        return new MachineView(machine.DeviceId, listing.Info?.Name ?? machine.Name, RemoteSessionHost.Describe(host.State), listing.Info?.Os, listing.Info?.Version,
            machine.Addresses.FirstOrDefault(), string.Join(' ', display.Split(' ').Take(2)), machine.Fingerprint.ToShortWords(), listing.Sessions.Count,
            host.LastSeen?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), host.State == RemoteMachineState.Online ? null : host.Problem,
            [.. machine.Permissions], machine.PairedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }
}

/// <summary>A request about the machines, from a page of a host lifetime.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
internal sealed record MachinesRequest(string ExpectedEpoch);

/// <summary>A paired machine as the page shows it.</summary>
/// <param name="Id">Its device id.</param>
/// <param name="Name">Its name.</param>
/// <param name="State"><c>connecting</c>, <c>online</c>, <c>offline</c> or <c>unauthorized</c>.</param>
/// <param name="Os">Its system, once it said it.</param>
/// <param name="Version">The version of CodeAlta it runs, once it said it.</param>
/// <param name="Address">The address tried first.</param>
/// <param name="Fingerprint">The start of the fingerprint of its key.</param>
/// <param name="Words">The six words of its key.</param>
/// <param name="Sessions">How many sessions it listed last.</param>
/// <param name="LastSeen">When it last answered; null when it did not since the start.</param>
/// <param name="Problem">Why it is not online, in words; null while online.</param>
/// <param name="Permissions">What this device may do on it.</param>
/// <param name="PairedAt">When this device paired with it.</param>
internal sealed record MachineView(string Id, string Name, string State, string? Os, string? Version, string? Address, string Fingerprint, string Words,
    int Sessions, string? LastSeen, string? Problem, string[] Permissions, string PairedAt);

/// <summary>The paired machines.</summary>
/// <param name="Status"><c>ok</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="HostEpoch">The epoch of the host.</param>
/// <param name="Machines">The machines, in the order they were paired.</param>
internal sealed record MachinesList(string Status, string? HostEpoch, MachineView[] Machines);

/// <summary>A pairing link the person pasted.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Link">The <c>codealta-pair://</c> link.</param>
internal sealed record MachinePairLinkRequest(string ExpectedEpoch, string Link);

/// <summary>What a pairing link names.</summary>
/// <param name="Status"><c>ok</c>, <c>invalid_link</c>, <c>expired</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Name">The name of the machine.</param>
/// <param name="DeviceId">Its device id.</param>
/// <param name="Words">The six words of its key, to compare with the ones <c>altanode pair</c> shows.</param>
/// <param name="Addresses">Where it listens.</param>
/// <param name="ExpiresAt">When the link expires.</param>
/// <param name="AlreadyPaired">Whether this device paired with it already.</param>
internal sealed record MachinePairPreview(string Status, string? Name, string? DeviceId, string? Words, string[] Addresses, string? ExpiresAt, bool AlreadyPaired)
{
    internal static MachinePairPreview Refused(string status) => new(status, null, null, null, [], null, false);
}

/// <summary>The outcome of a pairing.</summary>
/// <param name="Status">
/// <c>ok</c>, <c>invalid_link</c>, <c>expired</c>, <c>link_refused</c> (the machine refused the link: used, replaced or
/// expired there), <c>refused</c> (another key, or this device was refused), <c>unreachable</c>, <c>stale_epoch</c> or
/// <c>unavailable</c>.
/// </param>
/// <param name="Machine">The machine, once paired.</param>
/// <param name="Message">What the failure said, when it said something.</param>
internal sealed record MachinePairResult(string Status, MachineView? Machine, string? Message);

/// <summary>A request about one machine.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The device id of the machine.</param>
internal sealed record MachineRequest(string ExpectedEpoch, string MachineId);

/// <summary>The outcome of an action on a machine: <c>ok</c>, <c>not_found</c>, <c>write_failed</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</summary>
/// <param name="Status">The outcome.</param>
internal sealed record MachineActionResult(string Status);

/// <summary>A new session on a machine.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The device id of the machine.</param>
/// <param name="ClientRequestId">The key of the creation: the same key again is the same session.</param>
/// <param name="ProjectId">The project of the machine; null for a session outside a project.</param>
/// <param name="Prompt">The first prompt; null for none.</param>
internal sealed record MachineNewSessionRequest(string ExpectedEpoch, string MachineId, string ClientRequestId, string? ProjectId, string? Prompt);

/// <summary>The outcome of a new session on a machine.</summary>
/// <param name="Status"><c>ok</c>, the code of what the machine refused, the state of a machine that is not online, or <c>not_found</c>.</param>
/// <param name="SessionId">The new session.</param>
/// <param name="Message">What the machine said of a refusal.</param>
internal sealed record MachineNewSessionResult(string Status, string? SessionId, string? Message);

/// <summary>A change of the machines.</summary>
/// <param name="Kind"><c>changed</c>.</param>
internal sealed record MachinesEvent(string Kind);
