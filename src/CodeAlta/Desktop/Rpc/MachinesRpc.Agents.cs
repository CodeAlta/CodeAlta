using CodeAlta.Desktop.Fleet;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Sandbox;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Whether agents of other machines may start sessions and send prompts: per space, the sandbox their sessions
/// run in at least, and whether they may bypass permissions. The desktop owner edits it under This computer.
/// A remote machine requires the manage permission on that machine and checks the policy against its own catalog.
/// </summary>
internal sealed partial class MachinesService
{
    // The longest space or profile name the machine takes in its policy.
    private const int MaximumAgentNameLength = 128;

    /// <summary>Reads the policy of a machine for the agents of other machines, with its spaces.</summary>
    [NeoRpcMethod("agents")]
    public async Task<MachineAgentsView> AgentsAsync(MachineAgentsRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return MachineAgentsView.Refused(refused);
        if (request!.MachineId is null)
        {
            if (Node is not { } node) return MachineAgentsView.Refused("unavailable");
            var (spaces, policy) = await node.ReadAgentPolicyAsync(cancellationToken).ConfigureAwait(false);
            return AgentPolicyView(policy, spaces.Select(static space => (space.Id, space.Name)));
        }

        if (request.MachineId is not { Length: > 0 and <= 64 } id) return MachineAgentsView.Refused("invalid_request");
        if (_fleet!.Find(id) is not { } host) return MachineAgentsView.Refused("not_found");
        try
        {
            var (info, policy) = await host.ManageAsync(ReadAgentPolicyAsync, changes: false, cancellationToken).ConfigureAwait(false);
            return AgentPolicyView(policy, info.Spaces.Select(static space => (space.Id, space.Name)));
        }
        catch (ObjectDisposedException) { return MachineAgentsView.Refused(RemoteSessionHost.Describe(host.State)); }
        catch (FleetException exception) { return MachineAgentsView.Refused(Code(exception)) with { Message = exception.Error.Message }; }
        catch (Exception exception) when (exception is IOException or TimeoutException) { return MachineAgentsView.Refused(RemoteSessionHost.MachineOfflineCode) with { Message = exception.Message }; }
    }

    private static MachineAgentsView AgentPolicyView(FleetAgentPolicyInfo policy, IEnumerable<(string Id, string Name)> spaces)
    {
        string[] sandboxes = [SandboxProfiles.Workspace, SandboxProfiles.Strict];
        if (policy.Sandbox is { } own && !sandboxes.Contains(own, StringComparer.OrdinalIgnoreCase)) sandboxes = [.. sandboxes, own];
        return new("ok", null, [.. spaces.Select(space => new MachineAgentSpaceView(space.Id, space.Name, policy.Spaces.Contains(space.Id, StringComparer.OrdinalIgnoreCase)))],
            policy.Sandbox, policy.AllowBypass, sandboxes);
    }

    /// <summary>Lets agents of other machines start sessions and send prompts in a space of a machine, or stops them.</summary>
    [NeoRpcMethod("setAgentSpace")]
    public Task<MachineManageResult> SetAgentSpaceAsync(MachineAgentSpaceRequest request, CancellationToken cancellationToken)
    {
        if (request?.Space?.Trim() is not { Length: > 0 and <= MaximumAgentNameLength } space) return Invalid(request?.ExpectedEpoch, "A space is named by its id.");
        return ChangeAgentPolicyAsync(request.ExpectedEpoch, request.MachineId, policy => policy with
        {
            Spaces = request.Allowed ? [.. policy.Spaces.Append(space).Distinct(StringComparer.OrdinalIgnoreCase)]
                : [.. policy.Spaces.Where(value => !string.Equals(value, space, StringComparison.OrdinalIgnoreCase))],
        }, cancellationToken);
    }

    /// <summary>Sets the sandbox profile the sessions agents of other machines start on a machine run in at least; null for their parent's.</summary>
    [NeoRpcMethod("setAgentSandbox")]
    public Task<MachineManageResult> SetAgentSandboxAsync(MachineAgentSandboxRequest request, CancellationToken cancellationToken)
    {
        if (request is null || request.Sandbox is { } named && named.Trim() is not { Length: > 0 and <= MaximumAgentNameLength })
            return Invalid(request?.ExpectedEpoch, "A sandbox is named by its profile.");
        return ChangeAgentPolicyAsync(request.ExpectedEpoch, request.MachineId, policy => policy with { Sandbox = request.Sandbox?.Trim() }, cancellationToken);
    }

    /// <summary>Lets the sessions agents of other machines start on a machine bypass permissions when their parent does, or never.</summary>
    [NeoRpcMethod("setAgentBypass")]
    public Task<MachineManageResult> SetAgentBypassAsync(MachineAgentBypassRequest request, CancellationToken cancellationToken)
    {
        if (request is null) return Invalid(null, "The request is empty.");
        return ChangeAgentPolicyAsync(request.ExpectedEpoch, request.MachineId, policy => policy with { AllowBypass = request.Allowed }, cancellationToken);
    }

    private static async Task<(FleetNodeInfo Info, FleetAgentPolicyInfo Policy)> ReadAgentPolicyAsync(FleetNodeClient client, CancellationToken cancellationToken)
    {
        var info = await client.GetInfoAsync(cancellationToken).ConfigureAwait(false);
        var policy = await client.GetAgentPolicyAsync(cancellationToken).ConfigureAwait(false);
        return (info, policy);
    }

    private async Task<MachineManageResult> ChangeAgentPolicyAsync(string? expectedEpoch, string? machineId,
        Func<FleetAgentPolicyInfo, FleetAgentPolicyInfo> change, CancellationToken cancellationToken)
    {
        if (machineId is not null)
        {
            return await ManageAsync(expectedEpoch, machineId, async (client, token) =>
            {
                var (info, policy) = await ReadAgentPolicyAsync(client, token).ConfigureAwait(false);
                var changed = change(WithoutRemovedSpaces(policy, info.Spaces.Select(static space => space.Id)));
                return await client.SetAgentPolicyAsync(changed, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }

        if (Refuse(expectedEpoch) is { } refused) return new(refused, null);
        if (Node is not { } node) return new("unavailable", null);
        try
        {
            await node.ChangeAgentPolicyAsync((spaces, policy) => change(WithoutRemovedSpaces(policy, spaces.Select(static space => space.Id))), cancellationToken)
                .ConfigureAwait(false);
            Changed();
            return new("ok", null);
        }
        catch (ObjectDisposedException) { return new("unavailable", null); }
        catch (FleetException exception) { return new(Code(exception), exception.Error.Message); }
    }

    // Drop spaces deleted from the catalog before applying the edit: the machine refuses an edit naming an unknown space.
    private static FleetAgentPolicyInfo WithoutRemovedSpaces(FleetAgentPolicyInfo policy, IEnumerable<string> spaces)
        => policy with { Spaces = [.. policy.Spaces.Where(space => spaces.Contains(space, StringComparer.OrdinalIgnoreCase))] };
}

/// <summary>Reads the policy for the agents of other machines.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The id of a managed remote machine, or null for this computer.</param>
internal sealed record MachineAgentsRequest(string ExpectedEpoch, string? MachineId);

/// <summary>The policy of a machine for the agents of other machines, as the person who manages it sees it.</summary>
/// <param name="Status"><c>ok</c>, <c>not_manager</c>, <c>offline</c>, or the code the machine answered.</param>
/// <param name="Message">What the machine said, when it refused or failed.</param>
/// <param name="Spaces">The spaces of the machine, each with whether agents may start sessions and send prompts there.</param>
/// <param name="Sandbox">The sandbox profile their sessions run in at least; null for the one their parent has.</param>
/// <param name="AllowBypass">Whether their sessions may bypass permissions when their parent does.</param>
/// <param name="Sandboxes">The profiles the page offers: the built-in ones, and the one the policy names.</param>
internal sealed record MachineAgentsView(string Status, string? Message, MachineAgentSpaceView[] Spaces, string? Sandbox, bool AllowBypass, string[] Sandboxes)
{
    internal static MachineAgentsView Refused(string status) => new(status, null, [], null, false, []);
}

/// <summary>A space of a machine, and whether agents of other machines may work there.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Allowed">Whether agents of other machines may start sessions and send prompts there.</param>
internal sealed record MachineAgentSpaceView(string Id, string Name, bool Allowed);

/// <summary>A space of a machine where agents of other machines are let in or kept out.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The id of a managed remote machine, or null for this computer.</param>
/// <param name="Space">The id of the space.</param>
/// <param name="Allowed">Whether they may work there.</param>
internal sealed record MachineAgentSpaceRequest(string ExpectedEpoch, string? MachineId, string Space, bool Allowed);

/// <summary>The sandbox the sessions agents of other machines start on a machine run in at least.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The id of a managed remote machine, or null for this computer.</param>
/// <param name="Sandbox">A sandbox profile of the machine; null for the one their parent has.</param>
internal sealed record MachineAgentSandboxRequest(string ExpectedEpoch, string? MachineId, string? Sandbox);

/// <summary>Whether the sessions agents of other machines start on a machine may bypass permissions.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="MachineId">The id of a managed remote machine, or null for this computer.</param>
/// <param name="Allowed">Whether they may, when their parent does.</param>
internal sealed record MachineAgentBypassRequest(string ExpectedEpoch, string? MachineId, bool Allowed);
