using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// Sessions of other machines that agents start and reach (doc/fleet/sessions-across-machines.md): the limits are the
// calling session's, computed here, where it runs; the machine applies its own policy on top, and decides.
internal sealed partial class BuiltInAltaCommandContributor
{
    private const string SessionMachinesCommandPath = "alta session machines";

    private static Command CreateSessionMachinesCommand(AltaCommandContext context)
    {
        var command = Leaf("machines", "List the other machines agents of this computer can start sessions on and send prompts to.");
        command.Add(async (_, _) => await HandleSessionMachinesAsync(context).ConfigureAwait(false));
        AddHelpText(
            command,
            "A machine takes sessions and prompts from the agents of this computer only in the spaces its owner allowed:",
            "it refuses them elsewhere (`agent_sessions_off`).",
            "Examples: `alta session machines`; `alta session create --machine <name> --stdin`.");
        return command;
    }

    private static async ValueTask<int> HandleSessionMachinesAsync(AltaCommandContext context)
    {
        if (context.Services.Get<SessionRuntimeService>() is not { RemoteChildren: { } remote } runtime)
            return Unsupported(context, "fleet.unavailable", "This host reaches no other machine.");
        if (await RefuseFleetToCallerAsync(context, runtime).ConfigureAwait(false) is { } refused) return refused;
        foreach (var machine in remote.ListMachines())
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.session.machine",
                version = 1,
                correlationId = context.CorrelationId,
                id = machine.Id,
                name = machine.Name,
                state = machine.State,
            });
        }

        return AltaExitCodes.Success;
    }

    // The fleet is never a way out of a sandbox (doc/fleet/security-and-privacy.md, "Remote execution", rule 8): a session
    // whose sandbox profile does not allow the fleet, or cannot be used, starts and drives nothing on another machine.
    // Null when allowed, or when no session calls.
    private static async Task<int?> RefuseFleetToCallerAsync(AltaCommandContext context, SessionRuntimeService runtime)
    {
        if (NormalizeOptionalText(context.Caller.SourceSessionId) is not { } callerId) return null;
        var floor = await runtime.GetFleetFloorAsync(callerId, context.CancellationToken).ConfigureAwait(false);
        return floor.Refusal is { } refusal ? PermissionDenied(context, "sandbox.fleetDenied", refusal) : null;
    }

    // A sandboxed session reaches on another machine only the sessions it started there, itself or through the sessions it
    // created here, as on this computer: another one could run less confined, or read for it what its sandbox keeps from
    // it. The machine then checks that the session runs at least as confined as the caller. Null when allowed.
    private static async Task<int?> RefuseRemoteSessionToSandboxAsync(AltaCommandContext context, IRemoteChildSessions remote, string sessionId, string callerId,
        SessionFleetFloor floor)
    {
        if (!floor.IsSandboxed || await DescendsFromAsync(context, remote.FindParentOf(sessionId), callerId).ConfigureAwait(false)) return null;
        return PermissionDenied(context, "sandbox.notOwnSession",
            $"This session is sandboxed (profile '{floor.Sandbox}'): on another machine it reaches only the sessions it started there, and session '{sessionId}' is not one of them. Ask the user.");
    }

    // A child on another machine: the calling session is its parent, and what it has is what the child may have at most.
    private static async ValueTask<int> HandleRemoteSessionCreateAsync(AltaCommandContext context, SessionCreateOptions options)
    {
        const string CommandPath = "alta session create";
        if (options.Worktree || options.NoWorktree || options.WorktreeBase is not null)
            return UsageError(context, "usage.machineWorktree", "Where a session of another machine works is that machine's: leave out --worktree, --no-worktree and --base with --machine.", CommandPath);
        if (options.NoParent)
            return UsageError(context, "usage.machineParent", "A session of another machine is the child of the calling session: leave out --no-parent.", CommandPath);
        if (options.Global && !string.IsNullOrWhiteSpace(options.Project))
            return UsageError(context, "usage.projectAndGlobal", "Use either --project or --global, not both.", CommandPath);
        if (options.Model.Tier is not null || options.Model.SameModelAsSessionId is not null)
            return UsageError(context, "usage.machineModel", "The tiers and sessions of this computer mean nothing on another machine: name its provider and model with --model-ref, or leave them to the machine.", CommandPath);
        var parentId = NormalizeOptionalText(context.Caller.SourceSessionId);
        if (parentId is null)
            return UsageError(context, "usage.machineCaller", "A session of another machine is started by an agent, as the child of its session: call this from a session.", CommandPath);
        if (NormalizeOptionalText(options.ParentSessionId) is { } named && !string.Equals(named, parentId, StringComparison.OrdinalIgnoreCase))
            return UsageError(context, "usage.machineParent", "A session of another machine is the child of the calling session only.", CommandPath);

        string? prompt = null;
        if (options.UseStdin || !string.IsNullOrWhiteSpace(options.Message))
        {
            var read = await ReadPromptAsync(context, new PromptOptions { Message = options.Message, UseStdin = options.UseStdin }, PromptDispatchKind.Send).ConfigureAwait(false);
            if (read.ExitCode != AltaExitCodes.Success) return read.ExitCode;
            prompt = read.Prompt;
        }

        context.ClaimSessionWork(parentId);
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime)) return AltaExitCodes.ServiceUnavailable;
        if (runtime.RemoteChildren is not { } remote) return Unsupported(context, "fleet.unavailable", "This host reaches no other machine: it starts sessions here only.");
        var parent = await runtime.TryGetResumableSessionAsync(parentId, context.CancellationToken).ConfigureAwait(false);
        if (parent is null) return NotFound(context, "session.notFound", $"Session '{parentId}' was not found.");

        // What the calling session has is the floor of the child: a profile without the fleet starts none, and a sandboxed
        // session starts one only in the project of its own repository, as it works here in the folder of its project.
        var floor = await runtime.GetFleetFloorAsync(parentId, context.CancellationToken).ConfigureAwait(false);
        if (floor.Refusal is { } fleetRefusal) return PermissionDenied(context, "sandbox.fleetDenied", fleetRefusal);
        if (floor.IsSandboxed && (options.Global || NormalizeOptionalText(options.Project) is not null))
            return PermissionDenied(context, "sandbox.projectDenied",
                $"This session is sandboxed (profile '{floor.Sandbox}'): on another machine it starts sessions in the project of its own repository only. Leave --project and --global out.");

        var space = await ResolveNewSessionSpaceAsync(context, options.Space, CommandPath).ConfigureAwait(false);
        if (space.ExitCode != AltaExitCodes.Success) return space.ExitCode;
        var parentOptions = await runtime.GetSessionOptionsAsync(parentId, context.CancellationToken).ConfigureAwait(false);
        if (parentOptions.Private)
            return PermissionDenied(context, "session.privateMachine", "This session is Private: nothing of it reaches another machine, so it starts no session there.");

        // Never asks the user less than the calling session.
        var parentMode = floor.PermissionMode;
        var permissionMode = NormalizeOptionalText(options.PermissionMode);
        if (permissionMode is not null && SessionPermissionModes.Rank(permissionMode) > SessionPermissionModes.Rank(parentMode))
            return PermissionDenied(context, "session.permissionModeDenied",
                $"This session runs in '{parentMode}': it cannot start a session in '{permissionMode}', which asks less, on another machine either.");

        // A sandbox at least as strict: the calling session's, or the one asked when it has none, with what it confines here.
        if (await RefuseSandboxChoiceAsync(context, runtime, options.Sandbox, null, CommandPath).ConfigureAwait(false) is { } sandboxRefused) return sandboxRefused;
        var (sandbox, confinement) = floor.IsSandboxed ? (floor.Sandbox, floor.Confinement)
            : NormalizeOptionalText(options.Sandbox) is { } asked ? (asked, runtime.ResolveSandboxConfinement(asked)) : (null, null);
        if (sandbox is not null && confinement is null)
            return UsageError(context, "usage.invalidSandbox", $"The sandbox profile '{sandbox}' cannot be used on this computer: name another one, or leave --sandbox out.", CommandPath);

        // Without the tools the calling session does without, by its settings and by the command line of its run.
        var disabled = (await runtime.GetDisabledToolsAsync(parentId, context.CancellationToken).ConfigureAwait(false))
            .Union(runtime.GetDisabledToolPatterns(parentId), StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();

        var (provider, model, effort) = SplitModelRef(options.Model.ModelRef);
        var result = await remote.CreateAsync(new RemoteChildRequest(parentId, options.Machine!.Trim())
        {
            Prompt = prompt,
            Project = NormalizeOptionalText(options.Project),
            Global = options.Global,
            Title = NormalizeOptionalText(options.Title),
            SpaceId = space.SpaceId ?? parent.SpaceId ?? "default",
            ProviderKey = NormalizeOptionalText(options.Model.ProviderKey) ?? provider,
            ModelId = NormalizeOptionalText(options.Model.ModelId) ?? model,
            ReasoningEffort = options.Model.ReasoningEffort?.ToString() ?? effort,
            AgentPromptId = NormalizeOptionalText(options.Model.PromptId),
            DisabledTools = disabled,
            PermissionMode = permissionMode ?? parentMode,
            Sandbox = sandbox,
            Confinement = confinement,
            SubSessions = parentOptions.SubSessions && !options.NoSubSessions,
        }, context.CancellationToken).ConfigureAwait(false);
        if (result.Status != "created") return MachineRefusal(context, result.Code, result.Message, result.MachineName ?? options.Machine);

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.created",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = result.SessionId,
            machine = result.MachineName,
            machineId = result.MachineId,
            providerKey = result.ProviderKey,
            projectId = result.ProjectId,
            spaceId = space.SpaceId ?? parent.SpaceId,
            parentSessionId = parentId,
            permissionMode = result.PermissionMode,
            sandbox = result.Sandbox,
            promptSent = result.PromptSent,
            note = result.PromptSent ? NotificationFollowUpGuidance
                : "The session waits for its first prompt: `alta session send <session-id> --stdin` sends it there.",
        });
        return AltaExitCodes.Success;
    }

    // A prompt to a session another machine lists: signed for the calling session, and the machine decides.
    private static async ValueTask<int> HandleRemoteSessionSendAsync(AltaCommandContext context, SessionRuntimeService runtime, IRemoteChildSessions remote,
        string sessionId, PromptOptions options, PromptDispatchKind kind, string prompt)
    {
        if (NormalizeOptionalText(context.Caller.SourceSessionId) is not { } sender)
            return Unsupported(context, "session.remoteCaller", "A prompt to a session of another machine is sent by an agent, from its session; a person sends it from that session's tab.");
        if (options.HasSelection || options.Sandbox is not null || options.PromptId is not null || options.AttachmentsPath is not null)
            return PermissionDenied(context, "session.remoteSelection", "An agent does not change the model, the mode, the sandbox or the agent prompt of a session of another machine, nor sends it files.");
        // The sender's confinement is the floor of the session it drives: refused here when its profile forbids the fleet
        // or the session is not one it started, and by the machine when the session runs less confined than the sender.
        var floor = await runtime.GetFleetFloorAsync(sender, context.CancellationToken).ConfigureAwait(false);
        if (floor.Refusal is { } fleetRefusal) return PermissionDenied(context, "sandbox.fleetDenied", fleetRefusal);
        if (await RefuseRemoteSessionToSandboxAsync(context, remote, sessionId, sender, floor).ConfigureAwait(false) is { } notOwn) return notOwn;
        var space = await ResolveNewSessionSpaceAsync(context, null, CommandPathForPrompt(kind)).ConfigureAwait(false);
        if (space.ExitCode != AltaExitCodes.Success) return space.ExitCode;
        var parent = await runtime.TryGetResumableSessionAsync(sender, context.CancellationToken).ConfigureAwait(false);
        if ((await runtime.GetSessionOptionsAsync(sender, context.CancellationToken).ConfigureAwait(false)).Private)
            return PermissionDenied(context, "session.privateMachine", "This session is Private: nothing of it reaches another machine.");
        var remoteKind = kind switch
        {
            PromptDispatchKind.Steer => "steer",
            PromptDispatchKind.Queue => "queue",
            _ when options.QueueIfBusy => "queue",
            _ => "send",
        };
        var result = await remote.SendAsync(new RemoteMessageRequest(sender, sessionId, prompt, remoteKind)
            { SpaceId = space.SpaceId ?? parent?.SpaceId ?? "default", Floor = floor }, context.CancellationToken).ConfigureAwait(false);
        if (result.Status == "refused") return MachineRefusal(context, result.Code, result.Message, result.MachineName);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.sent",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId,
            machine = result.MachineName,
            status = result.Status,
            runId = result.RunId,
            note = "The session runs on another machine. If it is your child, its final answer comes back to you: do not poll it.",
        });
        return AltaExitCodes.Success;
    }

    // `provider:model@effort`, any part may be missing.
    private static (string? Provider, string? Model, string? Effort) SplitModelRef(string? modelRef)
    {
        if (NormalizeOptionalText(modelRef) is not { } text) return (null, null, null);
        string? effort = null;
        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            effort = NormalizeOptionalText(text[(at + 1)..]);
            text = text[..at];
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? (NormalizeOptionalText(text), null, effort) : (NormalizeOptionalText(text[..colon]), NormalizeOptionalText(text[(colon + 1)..]), effort);
    }

    private static int MachineRefusal(AltaCommandContext context, string? code, string? message, string? machine)
    {
        var (exitCode, errorCode) = code switch
        {
            "machine_not_found" or "session_missing" or "project_missing" or "space_missing" or "unavailable" => (AltaExitCodes.NotFound, "machine.notFound"),
            "agent_sessions_off" or "forbidden" or "agent_request_denied" or "agent_request_invalid" => (AltaExitCodes.PolicyDenied, "machine.policyDenied"),
            "machine_offline" or "rate_limited" => (AltaExitCodes.ServiceUnavailable, "machine.unreachable"),
            "not_running" => (AltaExitCodes.Unsupported, "session.notRunning"),
            _ => (AltaExitCodes.Failure, "machine.refused"),
        };
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, errorCode, exitCode,
            $"{machine ?? "The machine"} refused ({code ?? "refused"}): {message ?? "nothing was done."}");
        return exitCode;
    }
}
