using System.Globalization;
using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page of a machine this computer manages (<see cref="DevicePermissions.Manage"/>): what it is and runs, what each
/// of its devices may do, its projects, its providers, its log and its updates, changed from here as its owner does on
/// it. Every call goes to the machine, which checks the permission itself at each call.
/// </summary>
internal sealed partial class MachinesService
{
    private const int MaximumPathLength = 4096;

    /// <summary>Reads what a machine is for the person who manages it: its state, devices, projects and providers.</summary>
    [NeoRpcMethod("management")]
    public async Task<MachineManagement> ManagementAsync(MachineRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineManagement.Refused(refused);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id) return MachineManagement.Refused("invalid_request");
        if (_fleet!.Find(id) is not { } host) return MachineManagement.Refused("not_found");
        try
        {
            var info = await host.ReadInfoAsync(cancellationToken).ConfigureAwait(false);
            if (!info.Permissions.Contains("manage", StringComparer.Ordinal))
                return MachineManagement.Refused("not_manager") with { Providers = Providers(info), ThisDevice = _fleet.DeviceId };
            var (status, devices, projects) = await host.ManageAsync(async (client, token) =>
            {
                var status = await client.GetStatusAsync(token).ConfigureAwait(false);
                var devices = await client.ListDevicesAsync(token).ConfigureAwait(false);
                var projects = await client.ListProjectsAsync(token).ConfigureAwait(false);
                return (status, devices, projects);
            }, changes: false, cancellationToken).ConfigureAwait(false);
            var self = _fleet.DeviceId;
            return new MachineManagement("ok", null, status.Version, status.SessionsAtWork, status.Updating, Describe(status.Update),
                [.. devices.Devices.OrderBy(static device => device.RevokedAt is null ? 0 : 1).ThenBy(static device => device.PairedAt)
                    .Select(device => new MachineDeviceView(device.DeviceId, device.Name, device.Fingerprint, [.. device.Permissions],
                        Format(device.PairedAt), device.RevokedAt is { } revoked ? Format(revoked) : null, device.Channels,
                        string.Equals(device.DeviceId, self, StringComparison.Ordinal)))],
                [.. projects.Projects.Where(static project => !project.Archived).OrderBy(static project => project.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(static project => new MachineProjectView(project.Id, project.Name, project.Path, project.RepositoryKey))],
                [.. projects.Diagnostics.Select(static diagnostic => diagnostic.Message)])
            {
                Providers = Providers(info),
                ThisDevice = self,
                Worktrees = status.Worktrees is { } worktrees
                    ? new MachineWorktreesView(worktrees.Count, (int)Math.Min(int.MaxValue, worktrees.Bytes / 1024), worktrees.IdleDays, Format(worktrees.MeasuredAt)) : null,
                Sandbox = status.Sandbox is { } sandbox ? new MachineSandboxView(sandbox.Profile, sandbox.Available, sandbox.Backend, sandbox.Reason, sandbox.Setup) : null,
                Service = status.Service is { } service ? MachineServiceView.Of(service) : null,
            };
        }
        catch (ObjectDisposedException) { return MachineManagement.Refused(RemoteSessionHost.Describe(host.State)); }
        catch (FleetException exception) { return MachineManagement.Refused(Code(exception)) with { Message = exception.Error.Message }; }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachineManagement.Refused(RemoteSessionHost.MachineOfflineCode) with { Message = exception.Message }; }
    }

    /// <summary>Gives a device of a machine a permission, or takes it away.</summary>
    [NeoRpcMethod("setDevicePermission")]
    public Task<MachineManageResult> SetDevicePermissionAsync(MachineDevicePermissionRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !DevicePermissionNames.All.Contains(request.Permission, StringComparer.Ordinal) || !ValidDevice(request.DeviceId))
            return Task.FromResult(Refuse(request?.ExpectedEpoch) is { } refused ? new MachineManageResult(refused, null) : new MachineManageResult("invalid_request", null));
        string[] change = [request.Permission];
        return ManageAsync(request.ExpectedEpoch, request.MachineId, (client, token) => client.SetDevicePermissionsAsync(
            new FleetDevicePermissionsParams(request.DeviceId, request.Allowed ? change : [], request.Allowed ? [] : change), token), cancellationToken);
    }

    /// <summary>Revokes a device of a machine: its key is refused there from now on.</summary>
    [NeoRpcMethod("revokeDevice")]
    public Task<MachineManageResult> RevokeDeviceAsync(MachineDeviceRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !ValidDevice(request.DeviceId))
            return Task.FromResult(Refuse(request?.ExpectedEpoch) is { } refused ? new MachineManageResult(refused, null) : new MachineManageResult("invalid_request", null));
        return ManageAsync(request.ExpectedEpoch, request.MachineId, (client, token) => client.RevokeDeviceAsync(request.DeviceId, token), cancellationToken);
    }

    /// <summary>Adds a folder of a machine to its projects.</summary>
    [NeoRpcMethod("addProject")]
    public Task<MachineManageResult> AddProjectAsync(MachineProjectRequest request, CancellationToken cancellationToken)
    {
        if (request?.Value is not { Length: > 0 and <= MaximumPathLength } path)
            return Task.FromResult(Refuse(request?.ExpectedEpoch) is { } refused ? new MachineManageResult(refused, null) : new MachineManageResult("invalid_request", null));
        return ManageAsync(request.ExpectedEpoch, request.MachineId, async (client, token) =>
            Note(await client.AddProjectAsync(new FleetProjectAddParams(path.Trim()), token).ConfigureAwait(false)), cancellationToken);
    }

    /// <summary>Clones a repository on a machine and adds it to its projects.</summary>
    [NeoRpcMethod("cloneProject")]
    public Task<MachineManageResult> CloneProjectAsync(MachineProjectRequest request, CancellationToken cancellationToken)
    {
        if (request?.Value is not { Length: > 0 and <= MaximumPathLength } url)
            return Task.FromResult(Refuse(request?.ExpectedEpoch) is { } refused ? new MachineManageResult(refused, null) : new MachineManageResult("invalid_request", null));
        return ManageAsync(request.ExpectedEpoch, request.MachineId, async (client, token) =>
            Note(await client.CloneProjectAsync(new FleetProjectCloneParams(url.Trim()), token).ConfigureAwait(false)), cancellationToken);
    }

    /// <summary>Removes a project from the catalog of a machine; its files stay.</summary>
    [NeoRpcMethod("removeProject")]
    public Task<MachineManageResult> RemoveProjectAsync(MachineProjectRequest request, CancellationToken cancellationToken)
    {
        if (request?.Value is not { Length: > 0 and <= MaximumNameLength } project)
            return Task.FromResult(Refuse(request?.ExpectedEpoch) is { } refused ? new MachineManageResult(refused, null) : new MachineManageResult("invalid_request", null));
        return ManageAsync(request.ExpectedEpoch, request.MachineId, (client, token) => client.RemoveProjectAsync(project, token), cancellationToken);
    }

    /// <summary>Reads the end of the log of a machine.</summary>
    [NeoRpcMethod("readLog")]
    public async Task<MachineLogView> ReadLogAsync(MachineLogRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null, false, null);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id || request.Lines is < 1 or > FleetNodeLogParams.MaxLines) return new("invalid_request", null, null, false, null);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, null, false, null);
        try
        {
            var log = await host.ManageAsync((client, token) => client.ReadLogAsync(request.Lines, token), changes: false, cancellationToken).ConfigureAwait(false);
            return new("ok", log.Text, log.Path, log.Truncated, null);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null, null, false, null); }
        catch (FleetException exception) { return new(Code(exception), null, null, false, exception.Error.Message); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, null, null, false, exception.Message); }
    }

    /// <summary>Checks the feed of a machine for an update, or installs the latest version there.</summary>
    [NeoRpcMethod("updateMachine")]
    public async Task<MachineUpdateView> UpdateMachineAsync(MachineUpdateRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null, null, null);
        if (request!.MachineId is not { Length: > 0 and <= 64 } id) return new("invalid_request", null, null, null, null);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null, null, null, null);
        try
        {
            var result = await host.ManageAsync((client, token) => client.UpdateNodeAsync(new FleetNodeUpdateParams(request.Check, null, request.Now), token),
                changes: !request.Check, cancellationToken).ConfigureAwait(false);
            return new(result.Status, result.Running, result.Latest, result.Target, result.Message);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null, null, null, null); }
        catch (FleetException exception) { return new(Code(exception), null, null, null, exception.Error.Message); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, null, null, null, exception.Message); }
    }

    private async Task<MachineManageResult> ManageAsync<T>(string? expectedEpoch, string? machineId, Func<FleetNodeClient, CancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        if (Refuse(expectedEpoch) is { } refused) return new(refused, null);
        if (machineId is not { Length: > 0 and <= 64 } id) return new("invalid_request", null);
        if (_fleet!.Find(id) is not { } host) return new("not_found", null);
        try
        {
            var result = await host.ManageAsync(call, changes: true, cancellationToken).ConfigureAwait(false);
            Changed();
            return new("ok", result is string note ? note : null);
        }
        catch (ObjectDisposedException) { return new(RemoteSessionHost.Describe(host.State), null); }
        catch (FleetException exception) { return new(Code(exception), exception.Error.Message); }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return new(RemoteSessionHost.MachineOfflineCode, exception.Message); }
    }

    // The codes the page tells apart: this device does not manage the machine (any more), or what the machine said.
    private static string Code(FleetException exception) => exception.Error.Code switch
    {
        FleetErrorCodes.Forbidden => "not_manager",
        FleetErrorCodes.Revoked or FleetErrorCodes.Unauthenticated => "unauthorized",
        var code => code,
    };

    private static string? Note(FleetProjectChange change) => change.Note;

    private static bool ValidDevice(string? deviceId) => deviceId is { Length: > 0 and <= 64 };

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static MachineProviderView[] Providers(FleetNodeInfo info)
        => [.. info.Providers.Select(static provider => new MachineProviderView(provider.Key, provider.Name, provider.IsDefault,
            provider.DefaultModelId is { } model ? provider.Models.FirstOrDefault(value => value.Id == model)?.Name ?? model : null, provider.Models.Count))];

    // Where the self-update of a machine stands, in words of the page: a switch that waits, or how the last one ended.
    private static MachineUpdateState? Describe(FleetUpdateStatus? status)
        => status is null ? null : new MachineUpdateState(status.Running, status.Installed, status.Pending?.Version, status.Pending?.Phase,
            status.LastOutcome?.Version, status.LastOutcome?.Outcome);
}

/// <summary>A machine as the person who manages it sees it.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c> (this computer does not manage it), <c>not_found</c>, <c>offline</c>, <c>stale_epoch</c>, or the code the machine answered.</param>
/// <param name="Message">What the machine said, when it refused or failed.</param>
/// <param name="Version">The version of CodeAlta it runs.</param>
/// <param name="SessionsAtWork">How many of its sessions work now.</param>
/// <param name="Updating">Whether it drains its sessions to switch to a new version.</param>
/// <param name="Update">Where its self-update stands; null for a machine that does not say.</param>
/// <param name="Devices">The devices paired with it, the revoked ones last.</param>
/// <param name="Projects">Its projects.</param>
/// <param name="Problems">What it could not read in its catalog.</param>
internal sealed record MachineManagement(string Status, string? Message, string? Version, int SessionsAtWork, bool Updating, MachineUpdateState? Update,
    MachineDeviceView[] Devices, MachineProjectView[] Projects, string[] Problems)
{
    /// <summary>Gets its ready providers, as it describes them to every paired device.</summary>
    public MachineProviderView[] Providers { get; init; } = [];

    /// <summary>Gets the device id of this computer, as the machine knows it (what its owner names to give it a permission).</summary>
    public string? ThisDevice { get; init; }

    /// <summary>Gets what the worktrees of its sessions take on its disk; null before the machine measured them, or for one that does not say.</summary>
    public MachineWorktreesView? Worktrees { get; init; }

    /// <summary>Gets the sandbox its sessions run in by default, and whether it can run there; null for a machine that does not say.</summary>
    public MachineSandboxView? Sandbox { get; init; }

    /// <summary>Gets the service that runs its node, and whether it still runs after a restart or a logout; null for a machine that does not say.</summary>
    public MachineServiceView? Service { get; init; }

    internal static MachineManagement Refused(string status) => new(status, null, null, 0, false, null, [], [], []);
}

/// <summary>The worktrees the sessions of a machine work in, as the machine measured them last.</summary>
/// <param name="Count">How many there are.</param>
/// <param name="Kilobytes">What their files take on its disk, in kilobytes.</param>
/// <param name="IdleDays">How many days a session is idle before the machine removes its worktree.</param>
/// <param name="MeasuredAt">When the machine measured them.</param>
internal sealed record MachineWorktreesView(int Count, int Kilobytes, int IdleDays, string MeasuredAt);

/// <summary>The sandbox the sessions of a machine run in by default, as the machine says it.</summary>
/// <param name="Profile">The profile a session no scope sandboxes runs in; null when such a session runs without a sandbox.</param>
/// <param name="Available">Whether the sandbox can run there.</param>
/// <param name="Backend">The sandbox of its system.</param>
/// <param name="Reason">Why it cannot run; null when it can.</param>
/// <param name="Setup">What to install or set up there for it to run; null when nothing is known.</param>
internal sealed record MachineSandboxView(string? Profile, bool Available, string Backend, string? Reason, string? Setup);

/// <summary>The service that runs the node of a machine, as the machine says it.</summary>
/// <param name="Installed">Whether a service runs the node.</param>
/// <param name="StartsAtBoot">Whether it starts when the machine starts, with nobody logged in.</param>
/// <param name="Lines">What it is and what happens after a restart and a logout, in words.</param>
/// <param name="Fix">The command that makes it start at boot, run on the machine; null when it does.</param>
/// <param name="FixWhere">Where to run it: <c>elevated</c> (an administrator terminal on Windows) or <c>terminal</c>; null with no command.</param>
internal sealed record MachineServiceView(bool Installed, bool StartsAtBoot, string[] Lines, string? Fix, string? FixWhere)
{
    internal static MachineServiceView Of(FleetNodeServiceStatus service)
    {
        // The node says the commands, with its own options and profile: the desktop shows them as they are.
        var fix = service.StartsAtBoot || string.IsNullOrWhiteSpace(service.Fix) ? null : service.Fix.Length <= 4096 ? service.Fix : null;
        return new(service.Installed, service.StartsAtBoot, [.. service.Lines], fix, fix is null ? null : service.Platform == "windows" ? "elevated" : "terminal");
    }
}

/// <summary>Where the self-update of a machine stands.</summary>
/// <param name="Running">The version that runs.</param>
/// <param name="Installed">The version of its installed entry point.</param>
/// <param name="PendingVersion">The version a switch waits to start; null for none.</param>
/// <param name="PendingPhase">Where that switch stands (<c>requested</c>, <c>draining</c>, <c>waiting_idle</c>...).</param>
/// <param name="LastVersion">The version of the last switch; null before the first.</param>
/// <param name="LastOutcome">How it ended (<c>confirmed</c>, <c>rolled_back</c>...).</param>
internal sealed record MachineUpdateState(string Running, string? Installed, string? PendingVersion, string? PendingPhase, string? LastVersion, string? LastOutcome);

/// <summary>A device paired with a machine.</summary>
/// <param name="Id">Its device id.</param>
/// <param name="Name">The name it gave itself.</param>
/// <param name="Fingerprint">The fingerprint of its key, as people read it.</param>
/// <param name="Permissions">What it may do there.</param>
/// <param name="PairedAt">When it paired.</param>
/// <param name="RevokedAt">When it was revoked; null while trusted.</param>
/// <param name="Channels">How many channels it has open there now.</param>
/// <param name="IsThisComputer">Whether it is this computer.</param>
internal sealed record MachineDeviceView(string Id, string Name, string Fingerprint, string[] Permissions, string PairedAt, string? RevokedAt, int Channels, bool IsThisComputer);

/// <summary>A project of a machine.</summary>
/// <param name="Id">Its id there.</param>
/// <param name="Name">Its name.</param>
/// <param name="Path">Its folder there.</param>
/// <param name="Repository">The repository of its checkout; null for a folder in no repository with a remote.</param>
internal sealed record MachineProjectView(string Id, string Name, string Path, string? Repository);

/// <summary>A provider ready on a machine.</summary>
/// <param name="Key">Its key.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsDefault">Whether it is the default one.</param>
/// <param name="DefaultModel">The name of its default model; null for none.</param>
/// <param name="Models">How many models it lists.</param>
internal sealed record MachineProviderView(string Key, string Name, bool IsDefault, string? DefaultModel, int Models);

/// <summary>A device of a machine, and a permission given or taken.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="DeviceId">The device.</param>
/// <param name="Permission"><c>view</c>, <c>drive</c>, <c>start</c>, <c>move</c>, <c>automate</c> or <c>manage</c>.</param>
/// <param name="Allowed">Whether it is given.</param>
internal sealed record MachineDevicePermissionRequest(string ExpectedEpoch, string MachineId, string DeviceId, string Permission, bool Allowed);

/// <summary>A device of a machine.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="DeviceId">The device.</param>
internal sealed record MachineDeviceRequest(string ExpectedEpoch, string MachineId, string DeviceId);

/// <summary>A project of a machine to add, clone or remove.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Value">The folder (add), the repository URL (clone) or the project id (remove).</param>
internal sealed record MachineProjectRequest(string ExpectedEpoch, string MachineId, string Value);

/// <summary>The end of the log of a machine to read.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Lines">How many lines; null for 200.</param>
internal sealed record MachineLogRequest(string ExpectedEpoch, string MachineId, int? Lines);

/// <summary>An update of a machine to check or install.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The machine.</param>
/// <param name="Check">Whether the feed is only read.</param>
/// <param name="Now">Whether the machine switches without waiting for its sessions.</param>
internal sealed record MachineUpdateRequest(string ExpectedEpoch, string MachineId, bool Check, bool Now);

/// <summary>The outcome of a change on a machine.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>offline</c>, or the code the machine answered.</param>
/// <param name="Message">What the machine said (a note for a project, or why it refused).</param>
internal sealed record MachineManageResult(string Status, string? Message);

/// <summary>The end of the log of a machine.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>offline</c>, or the code the machine answered.</param>
/// <param name="Text">The last lines.</param>
/// <param name="Path">Where the log is on the machine.</param>
/// <param name="Truncated">Whether the text was cut to its last bytes.</param>
/// <param name="Message">Why it could not be read.</param>
internal sealed record MachineLogView(string Status, string? Text, string? Path, bool Truncated, string? Message);

/// <summary>Where an update of a machine stands.</summary>
/// <param name="Status"><c>up_to_date</c>, <c>available</c>, <c>staged</c>, <c>unavailable</c>, <c>failed</c>, <c>not_manager</c>, <c>offline</c>...</param>
/// <param name="Running">The version it runs.</param>
/// <param name="Latest">The latest version of its feed.</param>
/// <param name="Target">The version found or staged.</param>
/// <param name="Message">What else to know, or why it failed.</param>
internal sealed record MachineUpdateView(string Status, string? Running, string? Latest, string? Target, string? Message);
