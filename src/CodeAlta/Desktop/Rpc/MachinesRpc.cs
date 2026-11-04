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
internal sealed partial class MachinesService
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

        // What an automation placed there runs with: its providers, which any device the machine lists may read.
        if (request.ProvidersOnly == true) return Choices(info, null) with { Projects = [] };
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

    /// <summary>Whether this computer receives sessions moved from its machines, and its name.</summary>
    [NeoRpcMethod("moveSettings")]
    public MachineMoveSettings MoveSettings(MachinesRequest request)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, false, null);
        if (_fleet!.Moves is not { } moves) return new("unavailable", false, null);
        return new("ok", moves.ReceivesMoves, moves.MachineName);
    }

    /// <summary>Lets this computer receive sessions moved from its machines, or not.</summary>
    [NeoRpcMethod("setReceiveMoves")]
    public MachineMoveSettings SetReceiveMoves(MachineReceiveMovesRequest request)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, false, null);
        if (_fleet!.Moves is not { } moves) return new("unavailable", false, null);
        try
        {
            moves.SetReceivesMoves(request!.Enabled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new("write_failed", moves.ReceivesMoves, moves.MachineName);
        }

        Changed();
        return new("ok", moves.ReceivesMoves, moves.MachineName);
    }

    /// <summary>
    /// Says what a move of a session to a machine (or to this computer) would meet: whether it is idle, its jobs, what
    /// stays behind, and whether the target has its project or can clone it.
    /// </summary>
    [NeoRpcMethod("movePreview")]
    public async Task<MachineMovePreview> MovePreviewAsync(MachineMoveRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineMovePreview.Refused(refused);
        if (_fleet!.Moves is not { } moves) return MachineMovePreview.Refused("unavailable");
        var move = request!;
        if (!Valid(move)) return MachineMovePreview.Refused("invalid_request");
        try
        {
            var preview = await moves.PreviewAsync(move.SessionId, move.SourceMachineId, move.TargetMachineId, cancellationToken).ConfigureAwait(false);
            var source = preview.Preview;
            var target = preview.Target;
            return new(source.Status, null, source.Busy, [.. source.Jobs], [.. source.Secrets], source.UnpushedCommits, source.Dirty, source.Branch, source.RepositoryKey,
                preview.TargetRefusal ?? target?.Status, target?.Code, target?.Message, target?.ProjectName, target?.ProjectPath, target?.CanClone ?? false,
                [.. target?.Warnings ?? []], preview.ReceivesMoves, moves.MachineName);
        }
        catch (KeyNotFoundException) { return MachineMovePreview.Refused("not_found"); }
        catch (ObjectDisposedException exception) { return MachineMovePreview.Refused(RemoteSessionHost.MachineOfflineCode, exception.Message); }
        catch (FleetException exception) { return MachineMovePreview.Refused(exception.Error.Code, exception.Error.Message); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachineMovePreview.Refused(RemoteSessionHost.MachineOfflineCode, exception.Message); }
    }

    /// <summary>
    /// Moves a session from where it runs to a machine, or to this computer: it is frozen where it was, its conversation
    /// and its work in progress go to the target, and it runs there from then on.
    /// </summary>
    [NeoRpcMethod("moveSession")]
    public async Task<MachineMoveResult> MoveSessionAsync(MachineMoveRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineMoveResult.Refused(refused);
        if (_fleet!.Moves is not { } moves) return MachineMoveResult.Refused("unavailable");
        var move = request!;
        if (!Valid(move)) return MachineMoveResult.Refused("invalid_request");
        try
        {
            // The move goes on when the page stops waiting: it stays recoverable whatever happens to this request.
            var result = await moves.MoveAsync(move.SessionId, move.SourceMachineId, move.TargetMachineId, move.CloneRepository, CancellationToken.None)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(result.Status, result.Code, result.Message, result.SessionId, result.Status == "finished" ? move.TargetMachineId : move.SourceMachineId,
                result.WorkFolder, [.. result.Warnings]);
        }
        catch (KeyNotFoundException) { return MachineMoveResult.Refused("not_found"); }
        catch (ObjectDisposedException exception) { return MachineMoveResult.Refused(RemoteSessionHost.MachineOfflineCode, exception.Message); }
        catch (FleetException exception) { return MachineMoveResult.Refused(exception.Error.Code, exception.Error.Message); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachineMoveResult.Refused(RemoteSessionHost.MachineOfflineCode, exception.Message); }
    }

    private static bool Valid(MachineMoveRequest request)
        => request.SessionId is { Length: > 0 and <= 128 } && request.SourceMachineId is null or { Length: > 0 and <= 64 }
            && request.TargetMachineId is null or { Length: > 0 and <= 64 } && !string.Equals(request.SourceMachineId, request.TargetMachineId, StringComparison.Ordinal);

    private event Action? ChangedLocally;

    private void Changed() => ChangedLocally?.Invoke();

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
        ChangedLocally += OnChanged;
        try
        {
            await foreach (var value in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
        }
        finally
        {
            _fleet.Changed -= OnChanged;
            ChangedLocally -= OnChanged;
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
            // What the machine says this device may do, once it said it: a machine reached through a server only grants what its node decides.
            [.. listing.Info?.Permissions ?? machine.Permissions], machine.PairedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), host.IsPaired,
            host.ChannelPath switch { CodeAlta.Fleet.Server.FleetChannelPath.Relayed => "relayed", CodeAlta.Fleet.Server.FleetChannelPath.Direct => "direct", _ => null });
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
/// <param name="Paired">Whether this device paired with it; false for a machine of an organization reached through the relay of its server.</param>
/// <param name="Channel">Where its channel goes while it is online: <c>direct</c> or <c>relayed</c>; null otherwise.</param>
internal sealed record MachineView(string Id, string Name, string State, string? Os, string? Version, string? Address, string Fingerprint, string Words,
    int Sessions, string? LastSeen, string? Problem, string[] Permissions, string PairedAt, bool Paired, string? Channel);

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
internal sealed record MachineNewSessionChoicesRequest(string ExpectedEpoch, string MachineId, string? LocalProjectId)
{
    // True to read only the providers of the machine, for an automation placed there: no session is started, so this
    // device needs no permission to start one; absent for a new session.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? ProvidersOnly { get; init; }
}

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

/// <summary>Whether this computer receives sessions moved from its machines.</summary>
/// <param name="Status"><c>ok</c>, <c>write_failed</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="ReceiveMoves">Whether it receives them.</param>
/// <param name="MachineName">The name of this computer, as moved sessions say it.</param>
internal sealed record MachineMoveSettings(string Status, bool ReceiveMoves, string? MachineName);

/// <summary>Lets this computer receive moved sessions, or not.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Enabled">Whether it receives them.</param>
internal sealed record MachineReceiveMovesRequest(string ExpectedEpoch, bool Enabled);

/// <summary>A move of a session, or the question of what it would meet.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="SessionId">The session.</param>
/// <param name="SourceMachineId">The machine it runs on; null for this computer.</param>
/// <param name="TargetMachineId">The machine it goes to; null for this computer.</param>
/// <param name="CloneRepository">Lets the target clone the repository when it has no project of it.</param>
internal sealed record MachineMoveRequest(string ExpectedEpoch, string SessionId, string? SourceMachineId, string? TargetMachineId, bool CloneRepository);

/// <summary>What a move would meet.</summary>
/// <param name="Status">
/// <c>ok</c>, or why the session cannot move: <c>session_missing</c>, <c>not_movable</c>, <c>no_remote</c>, <c>not_repository</c>,
/// <c>moving</c>, <c>relinquished</c>, <c>machine_offline</c>, <c>forbidden</c>, <c>not_found</c>, <c>stale_epoch</c>, <c>unavailable</c>.
/// </param>
/// <param name="Message">What was said of a refusal.</param>
/// <param name="Busy">What keeps the session busy (<c>working</c>, <c>waiting</c>, <c>background_running</c>); null when idle.</param>
/// <param name="Jobs">The background jobs that run: they do not move.</param>
/// <param name="Secrets">The changed files that look like secrets: they stay.</param>
/// <param name="UnpushedCommits">How many commits no remote has: they move.</param>
/// <param name="Dirty">Whether the working tree has changes: they move.</param>
/// <param name="Branch">The branch.</param>
/// <param name="RepositoryKey">The repository key the target finds its project by.</param>
/// <param name="TargetStatus"><c>accepted</c>, <c>refused</c>, <c>forbidden</c> or <c>unreachable</c>; null when not asked.</param>
/// <param name="TargetCode">Why the target refuses (<c>project_missing</c>, <c>session_exists</c>...).</param>
/// <param name="TargetMessage">The refusal in words.</param>
/// <param name="ProjectName">The project of the target the session goes to.</param>
/// <param name="ProjectPath">Its folder on the target.</param>
/// <param name="CanClone">Whether the target can clone the repository.</param>
/// <param name="TargetWarnings">What the target says without refusing.</param>
/// <param name="ReceiveMoves">Whether this computer receives moved sessions.</param>
/// <param name="MachineName">The name of this computer.</param>
internal sealed record MachineMovePreview(string Status, string? Message, string? Busy, string[] Jobs, string[] Secrets, int UnpushedCommits, bool Dirty, string? Branch,
    string? RepositoryKey, string? TargetStatus, string? TargetCode, string? TargetMessage, string? ProjectName, string? ProjectPath, bool CanClone,
    string[] TargetWarnings, bool ReceiveMoves, string? MachineName)
{
    internal static MachineMovePreview Refused(string status, string? message = null)
        => new(status, message, null, [], [], 0, false, null, null, null, null, null, null, null, false, [], false, null);
}

/// <summary>The outcome of a move.</summary>
/// <param name="Status">
/// <c>finished</c> (the session runs on the target), <c>aborted</c> or <c>refused</c> (it stays where it was), <c>offered</c>
/// (the connection was lost after the session was frozen: the move goes on when the machine is back), or a refusal of the
/// request (<c>machine_offline</c>, <c>not_found</c>, <c>stale_epoch</c>, <c>unavailable</c>, <c>invalid_request</c>).
/// </param>
/// <param name="Code">Why it was refused or undone.</param>
/// <param name="Message">The refusal in words.</param>
/// <param name="SessionId">The session.</param>
/// <param name="MachineId">The machine the session runs on now; null for this computer.</param>
/// <param name="WorkFolder">The folder it works in there.</param>
/// <param name="Warnings">What went wrong without stopping the move.</param>
internal sealed record MachineMoveResult(string Status, string? Code, string? Message, string? SessionId, string? MachineId, string? WorkFolder, string[] Warnings)
{
    internal static MachineMoveResult Refused(string status, string? message = null) => new(status, null, message, null, null, null, []);
}

/// <summary>A change of the machines.</summary>
/// <param name="Kind"><c>changed</c>.</param>
internal sealed record MachinesEvent(string Kind);
