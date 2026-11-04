using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Fleet.Protocol.Api;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// The secrets, variables and environments of a server (doc/fleet/secrets.md), for the Secrets page of a server: the values
// of each scope, which machines hold them, the environments and their rules, the approvals that wait, and Rotate. A secret
// is never read back: the page sends a value once, and the host seals it for the machines and the person's controllers.
internal sealed partial class FleetServerService
{
    private const int MaximumValue = 64 * 1024;

    /// <summary>The secrets, variables, environments and approvals of this computer's person at a server.</summary>
    [NeoRpcMethod("secrets")]
    public async Task<FleetSecretsOverview> SecretsAsync(FleetServerLinkRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return FleetSecretsOverview.Failed(refused, null);
        if (!CodeAlta.Fleet.Server.FleetServerLinkStore.IsValidId(request!.LinkId)) return FleetSecretsOverview.Failed("invalid_request", null);
        try
        {
            var read = await _servers!.ReadSecretsAsync(request.LinkId, cancellationToken).ConfigureAwait(false);
            string DeviceName(string id) => read.Devices.TryGetValue(id, out var device) ? device.Name : id;
            string PersonName(string id) => read.People.FirstOrDefault(person => person.Id == id)?.DisplayName ?? id;
            var environments = read.Environments.ToDictionary(static pair => pair.Environment.Id, StringComparer.Ordinal);
            string ScopeLabel(FleetSecretScopeDto scope) => scope.Kind switch
            {
                FleetSecretScopeKinds.Person => PersonName(read.PersonId),
                FleetSecretScopeKinds.Environment when scope.Id is { } id && environments.TryGetValue(id, out var environment) => environment.Environment.Name,
                _ when scope.Id is { } id && read.ScopeNames.TryGetValue(id, out var name) => name,
                _ => scope.Kind == FleetSecretScopeKinds.Project ? "A project not on this computer" : "A space not linked here",
            };
            FleetSecretHolderView Holder(string id) => new(id, DeviceName(id), read.Devices.TryGetValue(id, out var device) && device.Kind != FleetDeviceKinds.Controller);
            var secrets = read.Secrets.Select(secret => new FleetSecretView(secret.Id, secret.Kind, secret.Name, secret.Scope.Kind, secret.Scope.Id, ScopeLabel(secret.Scope),
                secret.Version, secret.HostOnly, DeviceName(secret.SetByDeviceId), secret.SetAt, [.. secret.Holders.Select(Holder)], [.. secret.Flagged.Select(Holder)],
                read.Manager.ReadVariable(secret))).ToArray();
            var environmentViews = read.Environments.Where(static pair => pair.Definition is not { Removed: true }).Select(pair => pair.Definition is { } definition
                ? new FleetEnvironmentView(pair.Environment.Id, definition.Name, (int)Math.Min(definition.Number, int.MaxValue), [.. definition.Approvers], [.. definition.Approvers.Select(PersonName)],
                    [.. definition.MachineLabels], [.. definition.Automations], true)
                : new FleetEnvironmentView(pair.Environment.Id, pair.Environment.Name, (int)Math.Min(pair.Environment.Version, int.MaxValue), [], [], [], [], false)).ToArray();
            var approvers = read.Environments.Where(static pair => pair.Definition is not null)
                .ToDictionary(static pair => pair.Environment.Id, static pair => pair.Definition!.Approvers, StringComparer.Ordinal);
            var approvals = read.Approvals.Select(approval => new FleetApprovalView(approval.Id, approval.EnvironmentName, DeviceName(approval.MachineId), approval.RunId, approval.Title,
                approval.Status, approval.RequestedAt, approval.Status == FleetApprovalStates.Pending && approvers.TryGetValue(approval.EnvironmentId, out var people)
                    && people.Contains(read.PersonId, StringComparer.Ordinal), CodeAlta.Fleet.Server.FleetSecretsManager.ReviewOf(approval))).ToArray();
            var spaces = Spaces is null ? [] : await Spaces.LoadAsync(cancellationToken).ConfigureAwait(false);
            var linked = read.ScopeNames;
            return new FleetSecretsOverview("ok", null, secrets, environmentViews, approvals,
                [.. read.Machines.Select(machine => new FleetSecretTarget(machine.DeviceId, DeviceName(machine.DeviceId)))],
                [.. read.People.Select(static person => new FleetSecretTarget(person.Id, person.DisplayName))],
                [.. spaces.Where(space => linked.Values.Contains(space.Name)).Select(static space => new FleetSecretTarget(space.Id, space.Name))],
                await ProjectTargetsAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return FleetSecretsOverview.Failed(failure.Status, failure.Message);
        }
    }

    /// <summary>Sets a secret or a variable of a scope for the machines given, sealed by this computer; the same name in the same scope is rotated.</summary>
    [NeoRpcMethod("setSecret")]
    public Task<FleetSecretActionResult> SetSecretAsync(FleetSecretSetRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (!FleetSecretKinds.IsKnown(request!.Kind) || request.Name is not { Length: > 0 and <= 128 } name || request.Value is not { Length: > 0 and <= MaximumValue } value
                || request.Machines is not { Length: <= 64 } machines)
                return new FleetSecretActionResult("invalid_request", null);
            var scope = await _servers!.ScopeAsync(request.LinkId, request.ScopeKind, request.ScopeId, cancellationToken).ConfigureAwait(false);
            await _servers.SecretsOf(request.LinkId).SetAsync(request.Kind, name.Trim(), scope, value, request.HostOnly, machines, cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Rotates a value: its next version, sealed for the machines that hold it and are still in the list.</summary>
    [NeoRpcMethod("rotateSecret")]
    public Task<FleetSecretActionResult> RotateSecretAsync(FleetSecretRotateRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (request!.Id is not { Length: > 0 and <= 64 } id || request.Value is not { Length: > 0 and <= MaximumValue } value) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.SecretsOf(request.LinkId).RotateAsync(id, value, cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Deletes a value and every copy of it.</summary>
    [NeoRpcMethod("deleteSecret")]
    public Task<FleetSecretActionResult> DeleteSecretAsync(FleetSecretRefRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (request!.Id is not { Length: > 0 and <= 64 } id) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.SecretsOf(request.LinkId).DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Gives a machine of the person its copy of a value, sealed by this computer from its own copy.</summary>
    [NeoRpcMethod("grantSecret")]
    public Task<FleetSecretActionResult> GrantSecretAsync(FleetSecretMachineRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (request!.Id is not { Length: > 0 and <= 64 } id || request.MachineId is not { Length: > 0 and <= 128 } machine) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.SecretsOf(request.LinkId).GrantAsync(id, machine, cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Takes a machine's copy of a value away.</summary>
    [NeoRpcMethod("withdrawSecret")]
    public Task<FleetSecretActionResult> WithdrawSecretAsync(FleetSecretMachineRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (request!.Id is not { Length: > 0 and <= 64 } id || request.MachineId is not { Length: > 0 and <= 128 } machine) return new FleetSecretActionResult("invalid_request", null);
            await _servers!.SecretsOf(request.LinkId).WithdrawAsync(id, machine, cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Signs and stores an environment of the person: a new one, or the next version of the one of that name.</summary>
    [NeoRpcMethod("setEnvironment")]
    public Task<FleetSecretActionResult> SetEnvironmentAsync(FleetEnvironmentSetRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (!FleetEnvironmentDocuments.IsName(request!.Name) || request.Approvers is not { Length: <= 32 } approvers || request.Labels is not { Length: <= 32 } labels
                || request.Automations is not { Length: <= 32 } automations)
                return new FleetSecretActionResult("invalid_request", null);
            await _servers!.SecretsOf(request.LinkId).PutEnvironmentAsync(request.Name, approvers, [.. labels.Select(static label => label.Trim()).Where(static label => label.Length > 0)],
                automations, request.Removed, cancellationToken).ConfigureAwait(false);
            return new FleetSecretActionResult("ok", null);
        });

    /// <summary>Approves a run that waits for an environment: this computer signs the approval of that run.</summary>
    [NeoRpcMethod("approveRun")]
    public Task<FleetSecretActionResult> ApproveRunAsync(FleetApprovalActionRequest request, CancellationToken cancellationToken)
        => SecretActionAsync(request?.ExpectedEpoch, request?.LinkId, async () =>
        {
            if (request!.ApprovalId is not { Length: > 0 and <= 64 } id || request.Review is not { Length: > 0 and <= 128 } review)
                return new FleetSecretActionResult("invalid_request", null);
            // The run signed is the one shown: an approval the server changed since is refused.
            try
            {
                await _servers!.SecretsOf(request.LinkId).ApproveAsync(id, review, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException changed)
            {
                return new FleetSecretActionResult("approval_changed", changed.Message);
            }

            return new FleetSecretActionResult("ok", null);
        });

    private async Task<FleetSecretActionResult> SecretActionAsync(string? expectedEpoch, string? linkId, Func<Task<FleetSecretActionResult>> act)
    {
        if (Refuse(expectedEpoch) is { } refused) return new(refused, null);
        if (!CodeAlta.Fleet.Server.FleetServerLinkStore.IsValidId(linkId)) return new("invalid_request", null);
        try
        {
            var result = await act().ConfigureAwait(false);
            if (result.Status == "ok") ChangedLocally?.Invoke();
            return result;
        }
        catch (Exception exception) when (Failure(exception) is { } failure)
        {
            return new(failure.Status, failure.Message);
        }
    }

    private async Task<FleetSecretTarget[]> ProjectTargetsAsync(CancellationToken cancellationToken)
    {
        if (Spaces is null) return [];
        return [.. (await Spaces.Projects.LoadAsync(cancellationToken).ConfigureAwait(false)).Where(static project => !project.Archived && !string.IsNullOrWhiteSpace(project.Id))
            .Select(static project => new FleetSecretTarget(project.Id, string.IsNullOrWhiteSpace(project.DisplayName) ? project.Name : project.DisplayName))];
    }
}

/// <summary>A machine or a device, as a value names it.</summary>
/// <param name="Id">The device.</param>
/// <param name="Name">Its name.</param>
/// <param name="Machine">Whether it is a machine (not a controller device of the person).</param>
internal sealed record FleetSecretHolderView(string Id, string Name, bool Machine);

/// <summary>A secret or a variable, as the page shows it: never the value of a secret.</summary>
/// <param name="Id">The id of the value.</param>
/// <param name="Kind"><c>secret</c> or <c>variable</c>.</param>
/// <param name="Name">Its name.</param>
/// <param name="ScopeKind"><c>person</c>, <c>space</c>, <c>project</c> or <c>environment</c>.</param>
/// <param name="ScopeId">The id of its scope at the server.</param>
/// <param name="ScopeLabel">The name of its scope, as this computer knows it.</param>
/// <param name="Version">Its version.</param>
/// <param name="HostOnly">Whether it goes only to host-side tools.</param>
/// <param name="SetBy">The device that set its current version.</param>
/// <param name="SetAt">When.</param>
/// <param name="Holders">The devices that hold its current version.</param>
/// <param name="Flagged">The revoked devices that held it: it needs a rotation.</param>
/// <param name="Value">The value of a variable, read back from the copy of this computer; null for a secret.</param>
internal sealed record FleetSecretView(string Id, string Kind, string Name, string ScopeKind, string? ScopeId, string ScopeLabel, int Version, bool HostOnly, string SetBy,
    DateTimeOffset SetAt, FleetSecretHolderView[] Holders, FleetSecretHolderView[] Flagged, string? Value);

/// <summary>An environment, as the page shows it.</summary>
/// <param name="Id">The environment.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">The version of its definition.</param>
/// <param name="Approvers">The people who approve its runs, by id.</param>
/// <param name="ApproverNames">Their names.</param>
/// <param name="Labels">The labels of the machines it runs on.</param>
/// <param name="Automations">The automations it is open to.</param>
/// <param name="Verified">Whether its definition verifies against the device list.</param>
internal sealed record FleetEnvironmentView(string Id, string Name, int Version, string[] Approvers, string[] ApproverNames, string[] Labels, string[] Automations, bool Verified);

/// <summary>An approval a run asked.</summary>
/// <param name="Id">The approval.</param>
/// <param name="Environment">The name of the environment.</param>
/// <param name="Machine">The name of the machine of the run.</param>
/// <param name="RunId">The run.</param>
/// <param name="Title">What the run is.</param>
/// <param name="Status"><c>pending</c> or <c>approved</c>.</param>
/// <param name="RequestedAt">When it was asked.</param>
/// <param name="MayApprove">Whether this computer's person approves it.</param>
/// <param name="Review">What the person reviews of it: the approval given must still be this one.</param>
internal sealed record FleetApprovalView(string Id, string Environment, string Machine, string RunId, string? Title, string Status, DateTimeOffset RequestedAt, bool MayApprove,
    string Review);

/// <summary>A machine, a person, a space or a project a value can name.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
internal sealed record FleetSecretTarget(string Id, string Name);

/// <summary>The secrets, variables, environments and approvals of a server.</summary>
/// <param name="Status"><c>ok</c>, the code of the server's refusal, <c>unreachable</c>, <c>not_found</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Message">What the failure said.</param>
/// <param name="Secrets">The secrets and variables.</param>
/// <param name="Environments">The environments.</param>
/// <param name="Approvals">The approvals, the newest first.</param>
/// <param name="Machines">The machines of the person.</param>
/// <param name="People">The people of the organization, who may approve.</param>
/// <param name="Spaces">The spaces linked to the server.</param>
/// <param name="Projects">The projects of this computer.</param>
internal sealed record FleetSecretsOverview(string Status, string? Message, FleetSecretView[] Secrets, FleetEnvironmentView[] Environments, FleetApprovalView[] Approvals,
    FleetSecretTarget[] Machines, FleetSecretTarget[] People, FleetSecretTarget[] Spaces, FleetSecretTarget[] Projects)
{
    /// <summary>Gets an overview of a failure.</summary>
    /// <param name="status">The status.</param>
    /// <param name="message">What it said.</param>
    /// <returns>The overview.</returns>
    public static FleetSecretsOverview Failed(string status, string? message) => new(status, message, [], [], [], [], [], [], []);
}

/// <summary>A secret or a variable to set.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Kind"><c>secret</c> or <c>variable</c>.</param>
/// <param name="Name">Its name.</param>
/// <param name="ScopeKind"><c>person</c>, <c>space</c>, <c>project</c> or <c>environment</c>.</param>
/// <param name="ScopeId">The space or the project on this computer, or the environment; null for the person.</param>
/// <param name="Value">The value, sent once.</param>
/// <param name="HostOnly">Whether it goes only to host-side tools.</param>
/// <param name="Machines">The machines allowed to use it.</param>
internal sealed record FleetSecretSetRequest(string ExpectedEpoch, string LinkId, string Kind, string Name, string ScopeKind, string? ScopeId, string Value, bool HostOnly,
    string[] Machines);

/// <summary>A value to rotate.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Id">The value.</param>
/// <param name="Value">The new value, sent once.</param>
internal sealed record FleetSecretRotateRequest(string ExpectedEpoch, string LinkId, string Id, string Value);

/// <summary>A value.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Id">The value.</param>
internal sealed record FleetSecretRefRequest(string ExpectedEpoch, string LinkId, string Id);

/// <summary>A value and a machine.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Id">The value.</param>
/// <param name="MachineId">The machine.</param>
internal sealed record FleetSecretMachineRequest(string ExpectedEpoch, string LinkId, string Id, string MachineId);

/// <summary>An environment to set.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="Name">Its name.</param>
/// <param name="Approvers">The people who approve its runs; empty for none.</param>
/// <param name="Labels">The labels of the machines it runs on; empty for every machine of the person.</param>
/// <param name="Automations">The automations it is open to; empty for every run.</param>
/// <param name="Removed">Whether it is taken away.</param>
internal sealed record FleetEnvironmentSetRequest(string ExpectedEpoch, string LinkId, string Name, string[] Approvers, string[] Labels, string[] Automations, bool Removed);

/// <summary>An approval to give.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="LinkId">The link.</param>
/// <param name="ApprovalId">The approval.</param>
/// <param name="Review">What the person reviewed of it (<see cref="FleetApprovalView.Review"/>).</param>
internal sealed record FleetApprovalActionRequest(string ExpectedEpoch, string LinkId, string ApprovalId, string Review);

/// <summary>The outcome of an action on the values of a server.</summary>
/// <param name="Status"><c>ok</c>, the code of the server's refusal, <c>unreachable</c>, <c>not_found</c>, <c>invalid_request</c>, <c>stale_epoch</c> or <c>unavailable</c>.</param>
/// <param name="Message">What the failure said.</param>
internal sealed record FleetSecretActionResult(string Status, string? Message);
