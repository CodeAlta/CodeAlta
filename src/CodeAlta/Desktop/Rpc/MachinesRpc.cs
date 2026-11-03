using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Catalog.SessionMoves;
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
    private const int MaximumNameLength = 256;
    // How long a new session waits to be listed by its machine, so that the next list of the page has it.
    private static readonly TimeSpan ListedWait = TimeSpan.FromSeconds(10);
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

    /// <summary>Creates the service over the fleet of the instance and the projects of its catalog.</summary>
    /// <param name="fleet">The paired machines.</param>
    /// <param name="epoch">The host epoch requests must name.</param>
    /// <param name="projects">The projects of this computer, whose repositories suggest the same project on a machine.</param>
    /// <returns>The service.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal static MachinesService Create(DesktopFleet fleet, string epoch, ProjectCatalog projects)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(projects);
        return new MachinesService(fleet, epoch)
        {
            LocalLocation = async (projectId, token) => await projects.GetByIdAsync(projectId, token).ConfigureAwait(false) is { Archived: false } project
                && !string.IsNullOrWhiteSpace(project.ProjectPath)
                ? await fleet.Locations.GetAsync(project.ProjectPath, token).ConfigureAwait(false)
                : null,
        };
    }

    /// <summary>Gets the repository of a project of this computer, by its id; null when the service suggests no project of a machine.</summary>
    internal Func<string, CancellationToken, Task<RepositoryLocation?>>? LocalLocation { get; init; }

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

    /// <summary>
    /// What a new session on a machine can be given: its projects and its ready providers, as it says now, and the project
    /// of that machine in the same repository as a project of this computer.
    /// </summary>
    [NeoRpcMethod("newSessionChoices")]
    public async Task<MachineNewSessionChoices> NewSessionChoicesAsync(MachineNewSessionChoicesRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineNewSessionChoices.Refused(refused);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || request.LocalProjectId is { Length: > MaximumNameLength }) return MachineNewSessionChoices.Refused("invalid_request");
        if (_fleet!.Find(id) is not { } host) return MachineNewSessionChoices.Refused("not_found");
        FleetNodeInfo info;
        try
        {
            info = await host.ReadInfoAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { return MachineNewSessionChoices.Refused(RemoteSessionHost.Describe(host.State)); }
        catch (FleetException exception) { return MachineNewSessionChoices.Refused(exception.Error.Code) with { Message = exception.Error.Message }; }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachineNewSessionChoices.Refused(RemoteSessionHost.MachineOfflineCode) with { Message = exception.Message }; }

        // What this device may do there now: the owner of the machine may have changed it since the pairing.
        if (!info.Permissions.Contains("start", StringComparer.Ordinal)) return MachineNewSessionChoices.Refused("not_allowed");
        var local = request.LocalProjectId is { Length: > 0 } localId && !localId.StartsWith(WorkspaceService.MachineScopePrefix, StringComparison.Ordinal) && LocalLocation is { } read
            ? await read(localId, cancellationToken).ConfigureAwait(false)
            : null;
        return Choices(info, local);
    }

    /// <summary>The choices of a machine as the page shows them, and the project of the same repository as <paramref name="local"/>.</summary>
    /// <param name="info">The machine, as it describes itself.</param>
    /// <param name="local">The repository of the project of this computer the person is in; null for none.</param>
    /// <returns>The choices.</returns>
    internal static MachineNewSessionChoices Choices(FleetNodeInfo info, RepositoryLocation? local)
    {
        ArgumentNullException.ThrowIfNull(info);
        var projects = info.Projects.Where(static project => project.Id is { Length: > 0 and <= MaximumNameLength })
            .OrderBy(static project => project.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        // The same folder of the same repository, as the Explorer groups the sessions; the only one, or none is suggested.
        var same = local is null ? [] : projects.Where(project => project.RepositoryKey is { } key
            && RepositoryKey.SameLocation(new RepositoryLocation(key, project.RepositoryPath ?? string.Empty), local)).ToArray();
        return new MachineNewSessionChoices("ok", Bounded(info.Name),
            [.. projects.Select(static project => new MachineProjectChoice(project.Id, Bounded(project.Name), project.RepositoryKey))],
            same.Length == 1 ? same[0].Id : null,
            [.. info.Providers.Where(static provider => provider.Key is { Length: > 0 and <= MaximumNameLength }).Select(static provider => new MachineProviderChoice(provider.Key,
                Bounded(provider.Name), provider.IsDefault, provider.DefaultModelId,
                [.. provider.Models.Where(static model => model.Id is { Length: > 0 and <= MaximumNameLength }).Select(static model => new MachineModelChoice(model.Id, Bounded(model.Name)))]))],
            null);

        static string Bounded(string value) => value.Length <= MaximumNameLength ? value : value[..MaximumNameLength];
    }

    /// <summary>Creates a session on a machine, in one of its projects, with its provider and model and its first prompt.</summary>
    [NeoRpcMethod("newSession")]
    public async Task<MachineNewSessionResult> NewSessionAsync(MachineNewSessionRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || request.ClientRequestId is not { Length: > 0 and <= 256 } key
            || request.ProjectId is { Length: > MaximumNameLength } || request.Prompt is { Length: > MaximumPromptLength }
            || request.ProviderKey is { Length: > MaximumNameLength } || request.ModelId is { Length: > MaximumNameLength } || request.ModelId is not null && request.ProviderKey is null)
            return new("invalid_request", null, null);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, null);
        var selection = request.ProviderKey is { Length: > 0 } provider ? new FleetModelSelection(provider, request.ModelId is { Length: > 0 } model ? model : null) : null;
        try
        {
            var created = await host.CreateRemoteSessionAsync(new FleetCreateSessionParams(request.ProjectId, null, null,
                string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt, selection), key, cancellationToken).ConfigureAwait(false);
            // The page opens the session from its next list: the machine is asked for it until it lists it.
            await host.WaitListedAsync(created.SessionId, ListedWait, cancellationToken).ConfigureAwait(false);
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
/// <param name="ProviderKey">The provider of the machine; null for its default.</param>
/// <param name="ModelId">The model of that provider; null for its default. Only with a provider.</param>
internal sealed record MachineNewSessionRequest(string ExpectedEpoch, string MachineId, string ClientRequestId, string? ProjectId, string? Prompt,
    string? ProviderKey = null, string? ModelId = null);

/// <summary>A request for what a new session on a machine can be given.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The device id of the machine.</param>
/// <param name="LocalProjectId">The project of this computer the person is in, whose repository the machine may have too; null for none.</param>
internal sealed record MachineNewSessionChoicesRequest(string ExpectedEpoch, string MachineId, string? LocalProjectId);

/// <summary>A project of a machine a new session can work in.</summary>
/// <param name="Id">Its id on the machine.</param>
/// <param name="Name">Its name.</param>
/// <param name="RepositoryKey">The repository of its checkout; null for a folder in no repository with a remote.</param>
internal sealed record MachineProjectChoice(string Id, string Name, string? RepositoryKey);

/// <summary>A model of a provider of a machine.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
internal sealed record MachineModelChoice(string Id, string Name);

/// <summary>A provider of a machine that is ready to run sessions.</summary>
/// <param name="Key">Its key on the machine.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsDefault">Whether a session that names no provider runs on it.</param>
/// <param name="DefaultModelId">The model a session takes when it names none; null when the provider chooses.</param>
/// <param name="Models">Its models.</param>
internal sealed record MachineProviderChoice(string Key, string Name, bool IsDefault, string? DefaultModelId, MachineModelChoice[] Models);

/// <summary>What a new session on a machine can be given.</summary>
/// <param name="Status">
/// <c>ok</c>, <c>not_allowed</c> (this device may not start sessions there), <c>not_found</c>, the state of a machine that
/// is not online (<c>offline</c>, <c>connecting</c>, <c>unauthorized</c>, <c>machine_offline</c>), the code of a refusal of
/// the machine, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.
/// </param>
/// <param name="MachineName">The name of the machine.</param>
/// <param name="Projects">Its projects, by name.</param>
/// <param name="SuggestedProjectId">Its project in the same folder of the same repository as the project of this computer; null for none or several.</param>
/// <param name="Providers">Its ready providers.</param>
/// <param name="Message">What the machine said of a refusal.</param>
internal sealed record MachineNewSessionChoices(string Status, string? MachineName, MachineProjectChoice[] Projects, string? SuggestedProjectId,
    MachineProviderChoice[] Providers, string? Message)
{
    internal static MachineNewSessionChoices Refused(string status) => new(status, null, [], null, [], null);
}

/// <summary>The outcome of a new session on a machine.</summary>
/// <param name="Status"><c>ok</c>, the code of what the machine refused, the state of a machine that is not online, or <c>not_found</c>.</param>
/// <param name="SessionId">The new session.</param>
/// <param name="Message">What the machine said of a refusal.</param>
internal sealed record MachineNewSessionResult(string Status, string? SessionId, string? Message);

/// <summary>A change of the machines.</summary>
/// <param name="Kind"><c>changed</c>.</param>
internal sealed record MachinesEvent(string Kind);
