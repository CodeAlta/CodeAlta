using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Channels;
using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.Protocol;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Sandbox;

namespace CodeAlta.Fleet.NodeApi;

// Sessions and prompts that agents of other machines ask of this node, on behalf of the person of the device that asks
// (doc/fleet/access-control.md, "Agents and access"; doc/fleet/security-and-privacy.md, "Remote execution"): each request
// is signed by that device, the node's policy is the final authority, and nothing is given more than its parent has.
public sealed partial class FleetNodeService
{
    /// <summary>Gets what the node keeps of the sessions agents of other machines started on it: its owner's policy, their lineage and their outbox.</summary>
    public FleetAgentSessions AgentSessions => _agents;

    private void RegisterAgents(FleetNodeCaller caller)
    {
        On(caller, FleetNodeMethods.AgentsDeliveriesTake, Access.View, FleetNodeJsonContext.Default.FleetAgentDeliveryTakeParams,
            FleetNodeJsonContext.Default.FleetAgentDeliveryList, TakeDeliveriesAsync, optionalParams: true);
        On(caller, FleetNodeMethods.AgentsDeliveriesAck, Access.View, FleetNodeJsonContext.Default.FleetAgentDeliveryAckParams,
            FleetNodeJsonContext.Default.FleetEmpty, AcknowledgeDeliveriesAsync);
    }

    private void RegisterAgentPolicy(FleetNodeCaller caller, Access access)
    {
        On(caller, FleetNodeMethods.ControlAgentsGet, access, FleetNodeJsonContext.Default.FleetEmpty, FleetNodeJsonContext.Default.FleetAgentPolicyInfo,
            (_, _, _) => Task.FromResult(_agents.ReadPolicy()), optionalParams: true);
        On(caller, FleetNodeMethods.ControlAgentsSet, access, FleetNodeJsonContext.Default.FleetAgentPolicyInfo, FleetNodeJsonContext.Default.FleetAgentPolicyInfo,
            SetAgentPolicyAsync);
    }

    // Who may change it is who manages the node: an agent never reaches this method (it is no session's to call).
    private async Task<FleetAgentPolicyInfo> SetAgentPolicyAsync(FleetNodeCall call, FleetAgentPolicyInfo parameters, CancellationToken cancellationToken)
    {
        if (parameters.Spaces is null || parameters.Spaces.Count > 64 || parameters.Spaces.Any(static space => string.IsNullOrWhiteSpace(space) || space.Length > 128))
            throw new FleetException(FleetErrorCodes.InvalidParams, null, "The policy names at most 64 spaces.");
        var spaces = await _host.SpaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var space in parameters.Spaces)
        {
            if (!spaces.Any(known => string.Equals(known.Id, space.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new FleetException(FleetNodeErrorCodes.Unavailable, null, $"The node has no space '{space}'.");
        }

        if (parameters.Sandbox is { } sandbox && !KnowsSandbox(sandbox.Trim()))
            throw new FleetException(FleetNodeErrorCodes.Unavailable, null, $"The node has no sandbox profile '{sandbox}'.");
        try
        {
            _agents.WritePolicy(parameters);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FleetException(FleetErrorCodes.InternalError, FleetErrorHints.Retry, $"The policy could not be written: {exception.Message}");
        }

        var policy = _agents.ReadPolicy();
        LogInfo($"The policy of agents of other machines changed ({(call.Caller.IsOwner ? "the owner" : call.Caller.DeviceId)}): spaces [{string.Join(", ", policy.Spaces)}], sandbox {policy.Sandbox ?? "as their parent"}, bypass {(policy.AllowBypass ? "allowed" : "never")}.");
        return policy;
    }

    private Task<FleetAgentDeliveryList> TakeDeliveriesAsync(FleetNodeCall call, FleetAgentDeliveryTakeParams parameters, CancellationToken cancellationToken)
        => Task.FromResult(new FleetAgentDeliveryList(_agents.Take(call.Caller.DeviceId, parameters.Limit ?? 50)));

    private Task<FleetEmpty> AcknowledgeDeliveriesAsync(FleetNodeCall call, FleetAgentDeliveryAckParams parameters, CancellationToken cancellationToken)
    {
        if (parameters.Ids is null || parameters.Ids.Count > 200) throw new FleetException(FleetErrorCodes.InvalidParams, null, "An acknowledgement names 1 to 200 deliveries.");
        try
        {
            _agents.Acknowledge(call.Caller.DeviceId, parameters.Ids);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FleetException(FleetErrorCodes.InternalError, FleetErrorHints.Retry, $"The outbox could not be written: {exception.Message}");
        }

        return Task.FromResult(new FleetEmpty());
    }

    // The signature, then what the policy of the node allows in the space of the request.
    private FleetAgentRequest VerifyAgent(FleetNodeCall call, FleetSignedAgentRequest? signed, string kind)
    {
        // The local control of a node, or a node that drives this one, signs for no person.
        if (call.Caller.IsOwner || string.Equals(call.Caller.ClientKind, FleetChannelProtocol.ControlClientKind, StringComparison.Ordinal))
            throw new FleetException(FleetErrorCodes.Forbidden, null, "Only a device of a person signs the requests of agents.");
        if (!FleetAgentRequestDocuments.TryVerify(signed, kind, _options.Identity.DeviceId, call.Caller.Fingerprint.ToString(), _options.TimeProvider.GetUtcNow(),
                out var request, out var reason))
        {
            LogWarn($"A request of an agent from {call.Caller.DeviceId} was refused: {reason}.");
            throw new FleetException(FleetNodeErrorCodes.AgentRequestInvalid, null, $"The node refuses the request of the agent: {reason}.");
        }

        return request;
    }

    private FleetAgentPolicyInfo RequireAgentSpace(string spaceId)
    {
        var policy = _agents.ReadPolicy();
        if (!policy.Spaces.Any(space => string.Equals(space, spaceId, StringComparison.OrdinalIgnoreCase)))
            throw new FleetException(FleetNodeErrorCodes.AgentSessionsOff, null,
                $"{_options.Name} does not accept sessions or prompts from agents of other machines in the space '{spaceId}': its owner turns it on (`altanode agents allow --space {spaceId}`, or Settings > Fleet > Machines).");
        return policy;
    }

    private static bool SameId(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    // A start: the space, the project and the prompt are the ones signed; the limits are the parent's, narrowed by the
    // policy of the node; the node refuses what it cannot hold.
    private Task<FleetCreateSessionResult> CreateAgentSessionAsync(FleetNodeCall call, FleetCreateSessionParams parameters)
    {
        var request = VerifyAgent(call, parameters.Agent, FleetAgentRequestKinds.Start);
        if (!SameId(parameters.SpaceId, request.SpaceId) || !SameId(parameters.ProjectId, request.ProjectId)
            || (parameters.Prompt is { } prompt ? request.TextHash != FleetAgentRequestDocuments.HashText(prompt) : request.TextHash is not null))
            throw new FleetException(FleetNodeErrorCodes.AgentRequestInvalid, null, "The space, the project or the prompt is not the one the agent request signs.");
        var policy = RequireAgentSpace(request.SpaceId);
        var retryKey = RetryKey(call);
        return Once(call, FleetNodeJsonContext.Default.FleetCreateSessionResult, () => CreateAgentSessionCoreAsync(call, retryKey, parameters, request, policy));
    }

    private async Task<FleetCreateSessionResult> CreateAgentSessionCoreAsync(FleetNodeCall call, string retryKey, FleetCreateSessionParams parameters,
        FleetAgentRequest request, FleetAgentPolicyInfo policy)
    {
        if (!_agents.TryUseNonce(request.Nonce))
            throw new FleetException(FleetNodeErrorCodes.AgentRequestInvalid, null, "The node refuses the request of the agent: it was seen before.");
        if (!_agents.TryAdmitStart(call.Caller.DeviceId))
            throw new FleetException(FleetErrorCodes.ServerBusy, FleetErrorHints.Retry,
                $"Agents of this device started {FleetAgentSessions.MaxStartsPerWindow} sessions on {_options.Name} in the last minutes: it takes no more for a while.");
        var limits = request.Limits!;
        if (await _host.SpaceCatalog.GetAsync(request.SpaceId).ConfigureAwait(false) is null)
            throw new FleetException(FleetNodeErrorCodes.Unavailable, null, $"The node has no space '{request.SpaceId}'.");
        ProjectDescriptor? project = null;
        if (request.ProjectId is { } projectId)
        {
            project = await _host.ProjectCatalog.GetByIdAsync(projectId).ConfigureAwait(false);
            if (project is null || project.Archived || !Directory.Exists(project.ProjectPath))
                throw new FleetException(FleetNodeErrorCodes.Unavailable, null, $"The node has no project '{projectId}'.");
        }

        foreach (var pattern in limits.DisabledTools)
        {
            if (AgentToolPatterns.Validate(pattern) is { } error)
                throw new FleetException(FleetNodeErrorCodes.AgentRequestInvalid, null, $"The tools the agent request does without do not read: {error}");
        }

        var provider = ResolveProvider(parameters.Selection?.ProviderKey, project?.ProjectPath);
        // The policy of the organization of the space narrows what the node allows: never more than either.
        var rules = RequirePolicyStart(request.SpaceId, provider, parameters.Selection?.ModelId, agent: true);
        var permissionMode = AgentPermissionMode(provider.ProviderId.Value, Server.FleetPolicyGuard.NarrowerMode(limits.PermissionMode, rules?.PermissionMode), policy);
        var sandbox = AgentSandbox(limits, policy.Sandbox ?? rules?.Sandbox);
        if (rules is not null && PolicySandboxProblem(sandbox, rules) is { } sandboxProblem)
            throw Refused($"{_options.Name} cannot run the session: {sandboxProblem}.");
        IReadOnlyList<string> toolsOff = [.. limits.DisabledTools.Concat(rules?.ToolsOff ?? []).Distinct(StringComparer.Ordinal)];
        if (toolsOff.Select(AgentToolPatterns.Validate).FirstOrDefault(static error => error is not null) is { } invalidTools)
            throw Refused($"The tools the policy of the organization turns off do not read: {invalidTools}");
        var sessionId = Guid.NewGuid().ToString();
        var createdBy = new AltaActorProvenance
        {
            Kind = "agent",
            SourceSessionId = request.ParentSessionId,
            SourceMachineId = call.Caller.DeviceId,
            SourceMachineName = request.ParentMachineName,
            CorrelationId = "fleet-agent:" + request.Nonce,
            CreatedAt = _options.TimeProvider.GetUtcNow(),
        };

        // Whose work it is is known before it attaches: the session of an agent of a member's machine is that member's, not the owner's.
        RecordPrincipal(call.Device, sessionId);
        // The tools it does without are known before it attaches, and kept with its lineage across restarts of the node.
        _host.RuntimeService.SetDisabledToolPatterns(sessionId, toolsOff.Count > 0 ? toolsOff : null);
        SessionViewDescriptor session;
        try
        {
            session = await _host.Commands.CreateDraftSessionAsync(project, provider, parameters.Title, createdBy, null, request.SpaceId, permissionMode, sessionId,
                request.ParentSessionId, limits.SubSessions ? null : new AgentSessionOptions { SubSessions = false }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            _host.RuntimeService.SetDisabledToolPatterns(sessionId, null);
            throw new FleetException(FleetNodeErrorCodes.NodeStopping, FleetErrorHints.Retry, "The node is stopping.");
        }

        _agents.Add(new FleetAgentChild(session.SessionId, request.ParentSessionId, call.Caller.DeviceId, request.ParentMachineName, request.SpaceId,
            toolsOff, request.Nonce, _options.TimeProvider.GetUtcNow()));
        Work.Record(session.SessionId, request.SpaceId, "agent", _options.TimeProvider.GetUtcNow());
        if (sandbox is not null)
        {
            var set = await _host.Commands.SetSandboxAsync(session.SessionId, sandbox).ConfigureAwait(false);
            if (set.Status != "ok")
            {
                // A session that should run sandboxed and cannot is never sent its prompt.
                LogWarn($"The session {session.SessionId} an agent of {request.ParentMachineName} started could not be sandboxed ({set.Status}).");
                throw new FleetException(FleetNodeErrorCodes.AgentRequestDenied, null, $"{_options.Name} could not give the session the sandbox '{sandbox}' ({set.Status}).");
            }
        }

        LogInfo($"Session {session.SessionId} started for the agent session {request.ParentSessionId} of {request.ParentMachineName} ({call.Caller.DeviceId}), " +
            $"space {request.SpaceId}, project {project?.Id ?? "none"}, mode {permissionMode ?? "provider"}, sandbox {sandbox ?? "none"}, " +
            $"{limits.DisabledTools.Count} tools off, depth {limits.Depth}.");
        FleetCommandResult? send = null;
        if (parameters.Prompt is { } prompt)
        {
            var selection = Selection(parameters.Selection, provider.ProviderId.Value, session.AgentPromptId);
            send = Admit(_host.Commands.AdmitSend(new OwnedTextSendRequest(retryKey, session.SessionId, prompt) { Selection = selection }));
        }

        return new FleetCreateSessionResult(session.SessionId, provider.ProviderId.Value, send) { PermissionMode = permissionMode, Sandbox = sandbox };
    }

    // The mode the session runs in: at most the parent's, never bypassing permissions unless the owner allows it, and one
    // the provider offers; a provider whose only mode asks less than that is refused.
    private string? AgentPermissionMode(string providerKey, string? ceiling, FleetAgentPolicyInfo policy)
    {
        var rank = SessionPermissionModes.Rank(ceiling);
        if (rank >= SessionPermissionModes.Rank(SessionPermissionModes.Bypass) && !policy.AllowBypass)
            rank = SessionPermissionModes.Rank(SessionPermissionModes.AcceptEdits);
        var choices = _host.Commands.GetNewSessionPermissionModes(providerKey);
        if (choices.Modes.Count == 0)
        {
            if (SessionPermissionModes.Rank(choices.DefaultMode) <= rank) return null;
            throw new FleetException(FleetNodeErrorCodes.AgentRequestDenied, null,
                $"The provider '{providerKey}' of {_options.Name} runs its sessions in '{choices.DefaultMode}', which asks less than the agent may.");
        }

        return choices.Modes.Where(mode => SessionPermissionModes.Rank(mode) <= rank).OrderByDescending(SessionPermissionModes.Rank).FirstOrDefault()
            ?? throw new FleetException(FleetNodeErrorCodes.AgentRequestDenied, null, $"The provider '{providerKey}' of {_options.Name} offers no mode that asks as much as the agent's.");
    }

    // The sandbox the session runs in (doc/fleet/security-and-privacy.md, "Remote execution", rules 4 and 8): a profile of
    // this node at least as strict as the parent's profile as its machine resolves it, and as the one the policy requires
    // as this node resolves it; the parent's name first, then the policy's. A profile of the same name is not enough: the
    // owner of this node may have given it more. The node refuses when none holds, or when it cannot start a sandbox.
    private string? AgentSandbox(FleetAgentLimits limits, string? required)
    {
        var parent = limits.Sandbox?.Trim();
        required = required?.Trim();
        if (parent is null && required is null) return null;
        var floor = limits.Confinement is { } wire ? FleetSandboxFloors.FromWire(wire) : null;
        var profiles = _host.RuntimeService.ReadSandboxSettings().Profiles;
        SandboxConfinement? Here(string name)
        {
            if (string.Equals(name, SandboxProfiles.Off, StringComparison.OrdinalIgnoreCase)) return null;
            try { return SandboxConfinement.Of(SandboxProfiles.Resolve(name, profiles)); }
            catch (SandboxProfileException) { return null; }
        }

        var policyFloor = required is null ? null
            : Here(required) ?? throw new FleetException(FleetNodeErrorCodes.AgentRequestDenied, null, $"{_options.Name} has no sandbox profile '{required}' that its policy requires.");
        string? sandbox = null;
        string? why = null;
        foreach (var name in new[] { parent, required }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Here(name) is not { } candidate)
            {
                why ??= $"it has no sandbox profile '{name}' that can be used";
                continue;
            }

            if ((floor is null ? null : candidate.WeakerThan(floor)) is { } weaker)
            {
                why ??= $"its profile '{name}' lets the session do more than its parent: {weaker}";
                continue;
            }

            if ((policyFloor is null ? null : candidate.WeakerThan(policyFloor)) is { } loose)
            {
                why ??= $"its profile '{name}' is weaker than the profile '{required}' its policy requires: {loose}";
                continue;
            }

            sandbox = name;
            break;
        }

        if (sandbox is null)
            throw new FleetException(FleetNodeErrorCodes.AgentRequestDenied, null, $"{_options.Name} cannot run the session as sandboxed as its parent: {why}.");
        var availability = _host.RuntimeService.GetSandboxAvailability();
        if (!availability.IsAvailable)
            throw new FleetException(FleetNodeErrorCodes.AgentRequestDenied, null, $"{_options.Name} cannot start a sandbox ({availability.Reason}): it does not run the session without one.");
        return sandbox;
    }

    private bool KnowsSandbox(string profile)
        => SandboxProfiles.BuiltIn.Any(builtIn => string.Equals(builtIn.Name, profile, StringComparison.OrdinalIgnoreCase))
            || _host.RuntimeService.ReadSandboxSettings().Profiles.Any(own => string.Equals(own.Name, profile, StringComparison.OrdinalIgnoreCase));

    // A prompt of an agent: signed for this session and this text, in a space the policy allows, never a change of model
    // or mode, and held for a person when the session bypasses permissions and no agent of the same parent started it.
    private async Task<string> AgentPromptAsync(FleetNodeCall call, FleetSignedAgentRequest? signed, string kind, string sessionId, string text,
        FleetModelSelection? selection, CancellationToken cancellationToken)
    {
        var request = VerifyAgent(call, signed, kind);
        if (!string.Equals(request.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) || request.TextHash != FleetAgentRequestDocuments.HashText(text))
            throw new FleetException(FleetNodeErrorCodes.AgentRequestInvalid, null, "The session or the text is not the one the agent request signs.");
        if (selection is not null)
            throw new FleetException(FleetNodeErrorCodes.AgentRequestDenied, null, "An agent of another machine does not change the model or the mode of a session.");
        RequireAgentSpace(request.SpaceId);
        if (Policy?.RulesFor(request.SpaceId) is { AgentStarts: false })
            throw Refused("The policy of the organization lets no agent start work on another machine, nor prompt it.");
        var session = await RequireViewedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!SameId(session.SpaceId ?? "default", request.SpaceId))
            throw new FleetException(FleetNodeErrorCodes.AgentRequestDenied, null, "An agent reaches only the sessions of its own space.");
        // The mode the session runs in, not the one it records: without one of its own, it runs in its provider's.
        var mode = await RequireAgentFloorAsync(session.SessionId, request.Floor!, cancellationToken).ConfigureAwait(false);
        var child = _agents.Find(session.SessionId);
        var ownChild = child is not null && string.Equals(child.ParentSessionId, request.ParentSessionId, StringComparison.OrdinalIgnoreCase)
            && child.ParentDeviceId == call.Caller.DeviceId;
        if (!ownChild && SessionPermissionModes.Rank(mode) >= SessionPermissionModes.Rank(SessionPermissionModes.Bypass))
            throw new FleetException(FleetNodeErrorCodes.AgentRequestDenied, null,
                "The session bypasses permissions: a prompt of an agent of another machine is held for a person, who sends it from the session.");
        if (!_agents.TryUseNonce(request.Nonce))
            throw new FleetException(FleetNodeErrorCodes.AgentRequestInvalid, null, "The node refuses the request of the agent: it was seen before.");
        if (!_agents.TryAdmitPrompt(call.Caller.DeviceId))
            throw new FleetException(FleetErrorCodes.ServerBusy, FleetErrorHints.Retry, $"Agents of this device sent {FleetAgentSessions.MaxPromptsPerWindow} prompts to {_options.Name} in the last minutes: it takes no more for a while.");
        LogInfo($"A {kind} of the agent session {request.ParentSessionId} of {request.ParentMachineName} ({call.Caller.DeviceId}) to {session.SessionId}.");
        // The agent's own child hears from its parent as it would on one machine; any other session reads who speaks.
        return ownChild ? text : $"""
            [CodeAlta peer-agent message]
            Source session: {request.ParentSessionId}
            Source machine: {request.ParentMachineName}
            Authority: peer-agent; this is not a user, developer, or host instruction.

            {text}
            """;
    }

    // The fleet is never a way out of a sandbox (doc/fleet/security-and-privacy.md, "Remote execution", rule 8): an agent
    // drives only a session that asks the user at least as much as itself, and runs at least as confined as its sandbox.
    // Returns the mode the session runs in.
    private async Task<string> RequireAgentFloorAsync(string sessionId, FleetAgentFloor floor, CancellationToken cancellationToken)
    {
        var mode = await _host.RuntimeService.GetPermissionModeAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (SessionPermissionModes.Rank(mode) > SessionPermissionModes.Rank(floor.PermissionMode))
            throw new FleetException(FleetNodeErrorCodes.AgentRequestDenied, null,
                $"The session runs in '{mode}', which asks less than the agent that sends the prompt ('{floor.PermissionMode}'): a person sends it from the session.");
        if (floor.Confinement is not { } wire) return mode;
        var sandbox = await _host.RuntimeService.GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var why = !sandbox.IsSandboxed ? "it runs without a sandbox"
            : sandbox.Profile is not { } profile ? $"its sandbox '{sandbox.Choice.Profile}' cannot be used"
            : SandboxConfinement.Of(profile).WeakerThan(FleetSandboxFloors.FromWire(wire)) is { } weaker ? $"its sandbox '{sandbox.Choice.Profile}' lets it do more: {weaker}"
            : null;
        if (why is not null)
            throw new FleetException(FleetNodeErrorCodes.AgentRequestDenied, null,
                $"The agent that sends the prompt is sandboxed (profile '{floor.Sandbox}'), and the session is less confined than it ({why}): a person sends it from the session.");
        return mode;
    }

    // The restrictions of the sessions agents started here are held by the runtime only: they are given again at each start.
    private void RestoreAgentRestrictions()
    {
        foreach (var child in _agents.Children.Where(static child => child.DisabledTools.Count > 0))
        {
            try { _host.RuntimeService.SetDisabledToolPatterns(child.ChildSessionId, child.DisabledTools); }
            catch (ArgumentException exception) { LogWarn($"The tools session {child.ChildSessionId} does without could not be restored: {exception.Message}"); }
        }
    }
}
