using System.Globalization;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// `alta fleet` (doc/fleet/console.md, "Agents and scripts"): what the desktop can do with the fleet, for agents, within the
// access of the person of this computer. The rules of doc/fleet/access-control.md, "Agents and access", hold here: access is
// read only for agents (devices and permissions are read, never changed), what would run unattended stays disabled until a
// person enables it (an agent places a disabled automation only), and a campaign of updates is started by a person.
// The fleet is never a way out of a sandbox (doc/fleet/security-and-privacy.md, rule 8): the read commands are open to a
// sandboxed session (AltaSandboxGate), every command that starts or changes work on another machine is refused to it at
// the host, and a session moves only what asks a person at least as much as it does.
// Secrets are names only: `alta fleet secret list` never shows a value, and no command of an agent sets one.
internal sealed partial class BuiltInAltaCommandContributor
{
    private const int DefaultFleetSessions = 50;
    private const int MaximumFleetSessions = 200;
    private const int DefaultStatisticsDays = 7;

    private static readonly string[] StatisticsGroups = ["machine", "project", "provider", "model", "day", "month"];

    private static readonly AltaCommandPolicy[] FleetPolicies =
    [
        Read("fleet machine list"),
        Read("fleet machine show"),
        Read("fleet machine sessions"),
        Read("fleet machine devices"),
        Read("fleet move status"),
        // The same move as `alta session move`.
        Mutating("fleet move start"),
        Read("fleet automation list"),
        Read("fleet automation runs"),
        Mutating("fleet automation place"),
        Mutating("fleet automation run"),
        Read("fleet statistics"),
        Read("fleet update status"),
        // Refused: a person starts a campaign.
        Mutating("fleet update start"),
        Read("fleet health"),
        Read("fleet secret list"),
    ];

    private static Command CreateFleetCommand(AltaCommandContext context)
    {
        var group = Group("fleet", "Use the fleet of CodeAlta: the machines this computer reaches, their sessions, the moves, where automations run, statistics, updates and health.");
        var machine = Group("machine", "Read the machines of the fleet: their state, version, capabilities, labels, readiness and load, their sessions, and the devices of those this computer manages.");
        machine.Add(CreateFleetMachineListCommand(context));
        machine.Add(CreateFleetMachineShowCommand(context));
        machine.Add(CreateFleetMachineSessionsCommand(context));
        machine.Add(CreateFleetMachineDevicesCommand(context));
        group.Add(machine);
        var move = Group("move", "Move a session to another machine with its conversation and its work in progress, or see what a move would meet.");
        move.Add(CreateFleetMoveStartCommand(context));
        move.Add(CreateFleetMoveStatusCommand(context));
        group.Add(move);
        var automation = Group("automation", "See where the automations run, place one on a machine, run one now, and read the runs the machines report.");
        automation.Add(CreateFleetAutomationListCommand(context));
        automation.Add(CreateFleetAutomationPlaceCommand(context));
        automation.Add(CreateFleetAutomationRunCommand(context));
        automation.Add(CreateFleetAutomationRunsCommand(context));
        group.Add(automation);
        group.Add(CreateFleetStatisticsCommand(context));
        var update = Group("update", "Read the versions of the fleet and its update campaigns. A person starts a campaign.");
        update.Add(CreateFleetUpdateStatusCommand(context));
        update.Add(CreateFleetUpdateStartCommand(context));
        group.Add(update);
        group.Add(CreateFleetHealthCommand(context));
        var secret = Group("secret", "List the secrets and variables of this computer or of a machine it manages, by name: never a value.");
        secret.Add(CreateFleetSecretListCommand(context));
        group.Add(secret);
        AddHelpText(
            group,
            "The fleet is this computer and the machines it is paired with or reaches through a CodeAlta Server. Every command works with the access of the person of this computer, never more.",
            "Start a session on a machine with `alta session create --machine <name> --project <project-id> --stdin`: it is a child of the calling session, with its sandbox and its permission mode at most; its answer comes back to you. Prompt it with `alta session send <session-id> --stdin`, read it with `alta session report <session-id>`.",
            "Rules: devices, permissions, grants and policies are read only for agents (a person changes them in Settings > Fleet); an agent places a disabled automation only, and a person enables it; a person starts an update campaign. The fleet is never a way out of a sandbox: a sandboxed session reads the fleet, and every command that starts or changes work elsewhere is refused to it; a sandbox whose profile does not allow the fleet (`fleet = \"never\"`, as `strict`) refuses every command of it (`sandbox.fleetDenied`).",
            "Examples:",
            "  `alta fleet machine list --ready`",
            "  `alta fleet machine show mac-mini`",
            "  `alta fleet move status <session-id> --machine mac-mini`",
            "  `alta fleet automation place <automation-id> --machine build-box`",
            "  `alta fleet statistics --days 30 --group-by machine,provider`",
            "  `alta fleet health`");
        return group;
    }

    private static Command CreateFleetMachineListCommand(AltaCommandContext context)
    {
        var online = false;
        var ready = false;
        var labels = new List<string>();
        var capabilities = new List<string>();
        string? provider = null;
        var command = Leaf("list", "List the machines: this computer first, then each machine with its state, version, capabilities, labels, readiness and load.");
        command.Add("online", "Only the machines online now.", value => online = value is not null);
        command.Add("ready", "Only the machines that take a new session from this computer now (`readiness` is `ready`).", value => ready = value is not null);
        command.Add("label=", "Only the machines with this label; repeat it for several.", value => { if (value is not null) labels.Add(value); });
        command.Add("capability=", "Only the machines that offer this capability, such as `sandbox` or `moves`; repeat it for several.", value => { if (value is not null) capabilities.Add(value); });
        command.Add("provider=", "Only the machines with a ready provider of this name.", value => provider = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, () => ValueTask.FromResult(HandleFleetMachineList(context, online, ready, labels, capabilities, provider))).ConfigureAwait(false));
        AddHelpText(
            command,
            "`readiness` says whether a machine takes a session now: `ready`, `not_allowed` (this computer may not start sessions there), `no_provider`, `updating`, or its state when it is not online. `working` is how many of its sessions work now, its load.",
            "`capabilities` and `labels` come from what the machine publishes to a server: empty for a machine reached directly only.",
            "Examples: `alta fleet machine list`; `alta fleet machine list --ready --label gpu`; `alta fleet machine list --capability sandbox`.");
        return command;
    }

    private static Command CreateFleetMachineShowCommand(AltaCommandContext context)
    {
        string? name = null;
        var command = Leaf("show", "Show one machine now: its state, its projects, its ready providers and models, and where its update stands.");
        command.Add("<machine>", "The machine: its name or its id, as `alta fleet machine list` shows them.", value => name = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () => await HandleFleetMachineShowAsync(context, name).ConfigureAwait(false)).ConfigureAwait(false));
        AddHelpText(
            command,
            "Asks the machine itself. The ids of its projects and the keys of its providers are what `alta session create --machine <name> --project <project-id> --model-ref <provider>:<model>` names.",
            "Example: `alta fleet machine show mac-mini`.");
        return command;
    }

    private static Command CreateFleetMachineSessionsCommand(AltaCommandContext context)
    {
        string? name = null;
        string? limit = null;
        var command = Leaf("sessions", "List the sessions a machine lists, the latest activity first.");
        command.Add("<machine>", "The machine: its name or its id.", value => name = value);
        command.Add("limit=", $"The most sessions to list, up to {MaximumFleetSessions}. Defaults to {DefaultFleetSessions}.", value => limit = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () => await HandleFleetMachineSessionsAsync(context, name, limit).ConfigureAwait(false)).ConfigureAwait(false));
        AddHelpText(
            command,
            "Read one with `alta session report <session-id>`; prompt one of your children with `alta session send <session-id> --stdin`: the machine decides what it accepts.",
            "Example: `alta fleet machine sessions mac-mini --limit 10`.");
        return command;
    }

    private static Command CreateFleetMachineDevicesCommand(AltaCommandContext context)
    {
        string? name = null;
        var command = Leaf("devices", "List the devices paired with a machine this computer manages, and what each may do there. Read only.");
        command.Add("<machine>", "The machine: its name or its id.", value => name = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () => await HandleFleetMachineDevicesAsync(context, name).ConfigureAwait(false)).ConfigureAwait(false));
        AddHelpText(
            command,
            "Only a machine that gives this computer the `manage` permission answers (`fleet.notAllowed` otherwise). An agent never changes what a device may do: a person does it in the page of the machine (Settings > Fleet > Machines).",
            "Example: `alta fleet machine devices mac-mini`.");
        return command;
    }

    private static Command CreateFleetMoveStartCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        string? machine = null;
        var clone = false;
        var command = Leaf("start", "Move a session to another machine, with its conversation and its work in progress: the same move as `alta session move`.");
        command.Add("<session-id>?", "CodeAlta session id. Defaults to the calling agent session when omitted.", value => sessionId = value);
        command.Add("machine=", "The machine: its name or its id; this computer's name brings a session of a machine here.", value => machine = value);
        command.Add("clone", "Let the machine clone the repository of the session when it has no project of it.", value => clone = value is not null);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () => await HandleSessionMoveAsync(context, FirstNonEmpty(sessionId, context.Caller.SourceSessionId), machine, clone, list: false,
            commandPath: "alta fleet move start").ConfigureAwait(false)).ConfigureAwait(false));
        AddHelpText(
            command,
            "Look first with `alta fleet move status <session-id> --machine <name>`. The calling session moves when its turn ends (`scheduled`); another session moves at once, while it is idle.",
            "An agent moves itself, a session of this computer that asks the user at least as much as it does, or one of its children on another machine. A sandboxed session moves nothing.",
            "Example: `alta fleet move start <session-id> --machine mac-mini`.");
        return command;
    }

    private static Command CreateFleetMoveStatusCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        string? machine = null;
        var command = Leaf("status", "Say where a session runs and, with --machine, what a move of it there would meet.");
        command.Add("<session-id>?", "CodeAlta session id. Defaults to the calling agent session when omitted.", value => sessionId = value);
        command.Add("machine=", "The machine it would move to: its name or its id.", value => machine = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () => await HandleFleetMoveStatusAsync(context, FirstNonEmpty(sessionId, context.Caller.SourceSessionId), machine).ConfigureAwait(false)).ConfigureAwait(false));
        AddHelpText(
            command,
            "`busy` says what keeps the session from moving now; `targetStatus` and `targetCode` say whether the machine takes it (`project_missing`: add --clone to the move when `canClone` is true); `jobs` and `staysBehind` do not move.",
            "Example: `alta fleet move status <session-id> --machine mac-mini`.");
        return command;
    }

    private static Command CreateFleetAutomationListCommand(AltaCommandContext context)
    {
        var command = Leaf("list", "List the automations with where each runs: this computer, or a machine and where its copy stands there.");
        command.Add(async (_, _) => await WithFleetFloorAsync(context, () => ValueTask.FromResult(HandleFleetAutomationList(context))).ConfigureAwait(false));
        AddHelpText(command, "`state` is `here`, `copied` (the machine runs it), `pending` (it is copied once the machine answers) or `failed` (`message` says why).");
        return command;
    }

    private static Command CreateFleetAutomationPlaceCommand(AltaCommandContext context)
    {
        string? id = null;
        string? machine = null;
        var here = false;
        var command = Leaf("place", "Make a disabled automation run on a machine, or on this computer again. A person enables it.");
        command.Add("<automation-id>", "Automation id from `alta fleet automation list`.", value => id = value);
        command.Add("machine=", "The machine it runs on: its name or its id.", value => machine = value);
        command.Add("here", "Run it on this computer again.", value => here = value is not null);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () => await HandleFleetAutomationPlaceAsync(context, id, machine, here).ConfigureAwait(false)).ConfigureAwait(false));
        AddHelpText(
            command,
            "The automation runs there by its triggers while this computer is closed. What would run unattended is enabled by a person: an agent places a disabled automation only (`fleet.needsPerson` otherwise), and the machine must let this computer automate.",
            "Examples: `alta fleet automation place <automation-id> --machine build-box`; `alta fleet automation place <automation-id> --here`.");
        return command;
    }

    private static Command CreateFleetAutomationRunCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = AltaSubSessionsGate.Leaf(context, "run", "Run an automation now where it runs, whatever its triggers: the same run as `alta automation run`.");
        command.Add("<automation-id>", "Automation id from `alta fleet automation list`.", value => id = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () => await HandleAutomationRunAsync(context, id).ConfigureAwait(false)).ConfigureAwait(false));
        AddHelpText(command, "An automation placed on a machine runs there. Follow it with `alta fleet automation runs <automation-id>`.");
        return command;
    }

    private static Command CreateFleetAutomationRunsCommand(AltaCommandContext context)
    {
        string? id = null;
        string? limit = null;
        var command = Leaf("runs", "List the runs of an automation, newest first, those its machine reported included.");
        command.Add("<automation-id>?", "Automation id. Without it, the runs of all the automations.", value => id = value);
        command.Add("limit=", $"The most runs to list, up to {MaximumAutomationRuns}. Defaults to 20.", value => limit = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, () => ValueTask.FromResult(HandleFleetAutomationRuns(context, id, limit))).ConfigureAwait(false));
        return command;
    }

    private static Command CreateFleetStatisticsCommand(AltaCommandContext context)
    {
        string? from = null;
        string? to = null;
        string? days = null;
        string? timeZone = null;
        string? groupBy = null;
        string? server = null;
        var command = Leaf("statistics", "Read the sums of what the person's machines did, from a server: runs, requests, tokens, tool calls and estimated costs.");
        command.Add("from=", "The first day, yyyy-MM-dd. Defaults to the last days of --days.", value => from = value);
        command.Add("to=", "The last day, yyyy-MM-dd. Defaults to today.", value => to = value);
        command.Add("days=", $"The number of days up to --to. Defaults to {DefaultStatisticsDays}.", value => days = value);
        command.Add("time-zone=", "The time zone of the days, such as Europe/Paris. Defaults to UTC.", value => timeZone = value);
        command.Add("group-by=", "Up to three of machine, project, provider, model, day, month, separated by commas.", value => groupBy = value);
        command.Add("server=", "The server: the id of its link or its address. Defaults to the one online.", value => server = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () => await HandleFleetStatisticsAsync(context, from, to, days, timeZone, groupBy, server).ConfigureAwait(false)).ConfigureAwait(false));
        AddHelpText(
            command,
            "The record `alta.fleet.statistics` has the totals; each `alta.fleet.statistics.group` the sums of one group, the largest first. A computer linked to no server has none (`fleet.noServer`).",
            "Examples: `alta fleet statistics`; `alta fleet statistics --days 30 --group-by machine,provider`.");
        return command;
    }

    private static Command CreateFleetUpdateStatusCommand(AltaCommandContext context)
    {
        string? server = null;
        var command = Leaf("status", "Read the versions of the server and of its machines, the update policy and the recent campaigns.");
        command.Add("server=", "The server: the id of its link or its address. Defaults to the one online.", value => server = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () => await HandleFleetUpdateStatusAsync(context, server).ConfigureAwait(false)).ConfigureAwait(false));
        AddHelpText(command, "The version of one paired machine and where its own update stands is in `alta fleet machine show <machine>`.");
        return command;
    }

    private static Command CreateFleetUpdateStartCommand(AltaCommandContext context)
    {
        var command = Leaf("start", "Refused: a person starts an update campaign, in Settings > Fleet > Server > Updates.");
        command.Add((_, _) => ValueTask.FromResult(PermissionDenied(context, "fleet.needsPerson",
            "An update campaign replaces CodeAlta on the machines of the fleet: a person starts it, in Settings > Fleet > Server > Updates. Tell the user which version and machines you would update.")));
        return command;
    }

    private static Command CreateFleetHealthCommand(AltaCommandContext context)
    {
        var command = Leaf("health", "Say how the fleet stands: each server connection, and each machine that does not take sessions now, with why.");
        command.Add(async (_, _) => await WithFleetFloorAsync(context, () => ValueTask.FromResult(HandleFleetHealth(context))).ConfigureAwait(false));
        AddHelpText(command, "The summary `alta.fleet.health` counts the servers online and the machines ready. A machine that is offline or unauthorized needs its owner; one `not_allowed` needs the `start` permission from its owner.");
        return command;
    }

    private static Command CreateFleetSecretListCommand(AltaCommandContext context)
    {
        string? machine = null;
        var command = Leaf("list", "List the secrets and variables of this computer, or of a machine this computer manages, by name: never a value.");
        command.Add("<machine>?", "The machine: its name or its id. Defaults to this computer.", value => machine = value);
        command.Add(async (_, _) => await WithFleetFloorAsync(context, async () => await HandleFleetSecretListAsync(context, machine).ConfigureAwait(false)).ConfigureAwait(false));
        AddHelpText(
            command,
            "A session on a machine names a secret of that machine in its configuration as `${secret:NAME}` or `${var:NAME}`: check that the machine has it before you start work there. A machine answers only when it gives this computer the `manage` permission.",
            "An agent never reads nor sets a value: the user sets it in Settings > Secrets, in the page of the machine, or with `altanode secrets set <name>` there.",
            "Examples: `alta fleet secret list`; `alta fleet secret list mac-mini`.");
        return command;
    }

    private static async ValueTask<int> HandleFleetSecretListAsync(AltaCommandContext context, string? machine)
    {
        if (!context.TryGetRequired<IAltaFleet>(nameof(IAltaFleet), out var fleet)) return AltaExitCodes.ServiceUnavailable;
        var target = NormalizeOptionalText(machine);
        var secrets = await fleet.ListSecretsAsync(target, context.CancellationToken).ConfigureAwait(false);
        if (secrets.Status != "ok") return FleetFailure(context, secrets.Status, secrets.Message, target);
        foreach (var secret in secrets.Items)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.secret",
                version = 1,
                correlationId = context.CorrelationId,
                machine = secrets.Machine,
                kind = secret.Kind,
                name = secret.Name,
                reference = secret.Kind == "variable" ? $"${{var:{secret.Name}}}" : $"${{secret:{secret.Name}}}",
                secretVersion = secret.Version,
                setAt = secret.SetAt,
            });
        }

        WriteSummary(context, "alta.fleet.secret.summary", secrets.Items.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int HandleFleetMachineList(AltaCommandContext context, bool online, bool ready, IReadOnlyList<string> labels, IReadOnlyList<string> capabilities, string? provider)
    {
        if (!context.TryGetRequired<IAltaFleet>(nameof(IAltaFleet), out var fleet)) return AltaExitCodes.ServiceUnavailable;
        var providerName = NormalizeOptionalText(provider);
        var machines = fleet.ListMachines().Where(machine =>
            (!online || machine.State == "online")
            && (!ready || machine.Readiness == "ready")
            && labels.All(label => machine.Labels.Contains(label.Trim(), StringComparer.OrdinalIgnoreCase))
            && capabilities.All(capability => machine.Capabilities.Contains(capability.Trim(), StringComparer.OrdinalIgnoreCase))
            && (providerName is null || machine.Providers.Contains(providerName, StringComparer.OrdinalIgnoreCase))).ToArray();
        foreach (var machine in machines) AltaJsonlWriter.WriteRecord(context.Stdout, FleetMachineRecord(context, machine));
        WriteSummary(context, "alta.fleet.machine.summary", machines.Length, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleFleetMachineShowAsync(AltaCommandContext context, string? name)
    {
        if (!context.TryGetRequired<IAltaFleet>(nameof(IAltaFleet), out var fleet)) return AltaExitCodes.ServiceUnavailable;
        if (NormalizeOptionalText(name) is not { } machine) return UsageError(context, "usage.missingMachine", "Name the machine (`alta fleet machine list` lists them).", "alta fleet machine show");
        var detail = await fleet.ShowMachineAsync(machine, context.CancellationToken).ConfigureAwait(false);
        if (detail.Status != "ok" || detail.Machine is not { } found) return FleetFailure(context, detail.Status, detail.Message, machine);
        AltaJsonlWriter.WriteRecord(context.Stdout, FleetMachineRecord(context, found, detail.Update,
            found.IsThisComputer ? null : $"Start a session there: `alta session create --machine {found.Name} --project <project-id> --stdin`."));
        foreach (var project in detail.Projects)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.machine.project",
                version = 1,
                correlationId = context.CorrelationId,
                machine = found.Name,
                id = project.Id,
                name = project.Name,
                repository = project.RepositoryKey,
            });
        }

        foreach (var provider in detail.Providers)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.machine.provider",
                version = 1,
                correlationId = context.CorrelationId,
                machine = found.Name,
                key = provider.Key,
                name = provider.Name,
                isDefault = provider.IsDefault,
                defaultModel = provider.DefaultModel,
                models = provider.Models,
            });
        }

        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleFleetMachineSessionsAsync(AltaCommandContext context, string? name, string? limitText)
    {
        if (!context.TryGetRequired<IAltaFleet>(nameof(IAltaFleet), out var fleet)) return AltaExitCodes.ServiceUnavailable;
        const string CommandPath = "alta fleet machine sessions";
        if (NormalizeOptionalText(name) is not { } machine) return UsageError(context, "usage.missingMachine", "Name the machine (`alta fleet machine list` lists them).", CommandPath);
        if (!TryParseLimit(limitText, DefaultFleetSessions, MaximumFleetSessions, out var limit))
            return UsageError(context, "usage.invalidLimit", $"--limit is a number from 1 to {MaximumFleetSessions}.", CommandPath);
        var sessions = await fleet.ListSessionsAsync(machine, limit, context.CancellationToken).ConfigureAwait(false);
        if (sessions.Status != "ok") return FleetFailure(context, sessions.Status, sessions.Message, machine);
        foreach (var session in sessions.Items.Take(limit))
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.session",
                version = 1,
                correlationId = context.CorrelationId,
                machine = sessions.Machine,
                id = session.Id,
                title = session.Title,
                state = session.State,
                projectId = session.ProjectId,
                providerKey = session.ProviderKey,
                modelId = session.ModelId,
                parentSessionId = session.ParentSessionId,
                yours = session.ParentSessionId is { } parent && IsCaller(context, parent),
                updatedAt = session.UpdatedAt,
            });
        }

        WriteSummary(context, "alta.fleet.session.summary", Math.Min(limit, sessions.Items.Count), truncated: sessions.Items.Count > limit);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleFleetMachineDevicesAsync(AltaCommandContext context, string? name)
    {
        if (!context.TryGetRequired<IAltaFleet>(nameof(IAltaFleet), out var fleet)) return AltaExitCodes.ServiceUnavailable;
        if (NormalizeOptionalText(name) is not { } machine) return UsageError(context, "usage.missingMachine", "Name the machine (`alta fleet machine list` lists them).", "alta fleet machine devices");
        var devices = await fleet.ListDevicesAsync(machine, context.CancellationToken).ConfigureAwait(false);
        if (devices.Status != "ok") return FleetFailure(context, devices.Status, devices.Message, machine);
        foreach (var device in devices.Items)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.device",
                version = 1,
                correlationId = context.CorrelationId,
                machine = devices.Machine,
                id = device.Id,
                name = device.Name,
                permissions = device.Permissions,
                thisComputer = device.IsThisComputer,
                pairedAt = device.PairedAt,
                revokedAt = device.RevokedAt,
            });
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.fleet.device.summary",
            version = 1,
            correlationId = context.CorrelationId,
            count = devices.Items.Count,
            note = "Read only: a person gives or takes a permission, or revokes a device, in the page of the machine (Settings > Fleet > Machines).",
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleFleetMoveStatusAsync(AltaCommandContext context, string? sessionId, string? machine)
    {
        if (!context.TryGetRequired<IAltaFleet>(nameof(IAltaFleet), out var fleet)) return AltaExitCodes.ServiceUnavailable;
        if (NormalizeOptionalText(sessionId) is not { } id)
            return UsageError(context, "usage.missingSession", "Session id is required outside a session caller.", "alta fleet move status");
        var status = await fleet.GetMoveStatusAsync(id, NormalizeOptionalText(machine), context.CancellationToken).ConfigureAwait(false);
        if (status.Status is "session_missing" or "machine_not_found" or "not_found") return FleetFailure(context, status.Status, status.Message, machine);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.fleet.move",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = status.SessionId,
            status = status.Status,
            message = status.Message,
            machine = status.Machine,
            runsHere = status.RunsHere,
            target = status.Target,
            busy = status.Busy,
            targetStatus = status.TargetStatus,
            targetCode = status.TargetCode,
            project = status.Project,
            canClone = status.CanClone,
            jobs = status.Jobs,
            staysBehind = status.StaysBehind,
            warnings = status.Warnings,
        });
        return AltaExitCodes.Success;
    }

    private static int HandleFleetAutomationList(AltaCommandContext context)
    {
        if (!context.TryGetRequired<IAltaFleet>(nameof(IAltaFleet), out var fleet)) return AltaExitCodes.ServiceUnavailable;
        var automations = fleet.ListAutomationPlacements();
        foreach (var automation in automations)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.automation",
                version = 1,
                correlationId = context.CorrelationId,
                id = automation.Id,
                name = automation.Name,
                enabled = automation.Enabled,
                machineId = automation.MachineId,
                machine = automation.Machine,
                state = automation.State,
                message = automation.Message,
                copiedAt = automation.CopiedAt,
                nextRunAt = automation.NextRunAt,
            });
        }

        WriteSummary(context, "alta.fleet.automation.summary", automations.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleFleetAutomationPlaceAsync(AltaCommandContext context, string? id, string? machine, bool here)
    {
        const string CommandPath = "alta fleet automation place";
        if (await RefuseSettingsChangeAsync(context, "place an automation, whose sessions have the tools of their own space and agent prompt").ConfigureAwait(false) is { } settingsRefused)
            return settingsRefused;
        if (!context.TryGetRequired<IAltaFleet>(nameof(IAltaFleet), out var fleet)) return AltaExitCodes.ServiceUnavailable;
        if (NormalizeOptionalText(id) is not { } reference) return UsageError(context, "usage.missingAutomation", "An automation id is required.", CommandPath);
        var target = NormalizeOptionalText(machine);
        if ((target is null && !here) || (target is not null && here)) return UsageError(context, "usage.missingMachine", "Name the machine with --machine <name>, or bring it back with --here.", CommandPath);
        if (context.Services.Get<IAltaAutomations>() is { } automations && IsStartedByAutomation(context, automations)) return AutomationSessionDenied(context);
        var placements = fleet.ListAutomationPlacements();
        var automation = placements.FirstOrDefault(value => string.Equals(value.Id, reference, StringComparison.OrdinalIgnoreCase))
            ?? placements.SingleOrDefault(value => string.Equals(value.Name, reference, StringComparison.OrdinalIgnoreCase));
        if (automation is null) return AutomationNotFound(context, reference);
        // What runs unattended is enabled by a person, who signs it with their device: an agent moves a disabled one only.
        if (automation.Enabled && context.Caller.SourceSessionId is not null)
        {
            return PermissionDenied(context, "fleet.needsPerson",
                $"Automation '{automation.Name}' is enabled: where it runs unattended is chosen by a person, in the Automations tab. Disable it first with `alta automation disable {automation.Id}` and place it then; a person enables it again.");
        }

        var change = await fleet.PlaceAutomationAsync(automation.Id, target, context.CancellationToken).ConfigureAwait(false);
        if (change.Status != "ok") return FleetFailure(context, change.Status, change.Message, target);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.fleet.automation.placed",
            version = 1,
            correlationId = context.CorrelationId,
            id = automation.Id,
            name = automation.Name,
            machine = target,
            here = target is null,
            enabled = automation.Enabled,
            note = automation.Enabled ? null : "It stays disabled: a person enables it in the Automations tab. `alta fleet automation list` says when the machine has its copy.",
        });
        return AltaExitCodes.Success;
    }

    private static int HandleFleetAutomationRuns(AltaCommandContext context, string? id, string? limitText)
    {
        if (!context.TryGetRequired<IAltaFleet>(nameof(IAltaFleet), out var fleet)) return AltaExitCodes.ServiceUnavailable;
        if (!TryParseLimit(limitText, 20, MaximumAutomationRuns, out var limit))
            return UsageError(context, "usage.invalidLimit", $"--limit is a number from 1 to {MaximumAutomationRuns}.", "alta fleet automation runs");
        var reference = NormalizeOptionalText(id);
        var automationId = reference is null ? null
            : fleet.ListAutomationPlacements().FirstOrDefault(value => string.Equals(value.Id, reference, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value.Name, reference, StringComparison.OrdinalIgnoreCase))?.Id ?? reference.ToLowerInvariant();
        IEnumerable<AltaAutomationRun> local = context.Services.Get<IAltaAutomations>()?.ListRuns(automationId, limit) ?? [];
        var runs = local.Concat(fleet.ListPlacedRuns(automationId, limit)).DistinctBy(static run => run.Id, StringComparer.Ordinal)
            .OrderByDescending(static run => run.StartedAt).Take(limit).ToArray();
        foreach (var run in runs) AltaJsonlWriter.WriteRecord(context.Stdout, AutomationRunRecord(context, run));
        WriteSummary(context, "alta.automation.run.summary", runs.Length, truncated: runs.Length == limit);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleFleetStatisticsAsync(AltaCommandContext context, string? fromText, string? toText, string? daysText, string? timeZone,
        string? groupByText, string? server)
    {
        const string CommandPath = "alta fleet statistics";
        if (!context.TryGetRequired<IAltaFleet>(nameof(IAltaFleet), out var fleet)) return AltaExitCodes.ServiceUnavailable;
        if (!TryParseDay(toText, out var to, DateOnly.FromDateTime(DateTime.UtcNow)))
            return UsageError(context, "usage.invalidDay", "--to is a day, yyyy-MM-dd.", CommandPath);
        if (!TryParseLimit(daysText, DefaultStatisticsDays, 366, out var days))
            return UsageError(context, "usage.invalidDays", "--days is a number from 1 to 366.", CommandPath);
        if (!TryParseDay(fromText, out var from, to.AddDays(1 - days)) || from > to)
            return UsageError(context, "usage.invalidDay", "--from is a day, yyyy-MM-dd, no later than --to.", CommandPath);
        var groupBy = (groupByText ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static group => group.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (groupBy.Length > 3 || groupBy.Any(group => !StatisticsGroups.Contains(group)))
            return UsageError(context, "usage.invalidGroupBy", $"--group-by is up to three of {string.Join(", ", StatisticsGroups)}.", CommandPath);

        var statistics = await fleet.QueryStatisticsAsync(new AltaFleetStatisticsQuery(Day(from), Day(to), NormalizeOptionalText(timeZone), groupBy, NormalizeOptionalText(server)),
            context.CancellationToken).ConfigureAwait(false);
        if (statistics.Status != "ok" || statistics.Totals is not { } totals) return FleetFailure(context, statistics.Status, statistics.Message, null);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.fleet.statistics",
            version = 1,
            correlationId = context.CorrelationId,
            server = statistics.Server,
            from = Day(from),
            to = Day(to),
            timeZone = NormalizeOptionalText(timeZone) ?? "UTC",
            groupBy,
            totals = MeasuresRecord(totals),
            groups = statistics.Groups.Count,
            truncated = statistics.Truncated,
        });
        foreach (var group in statistics.Groups)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.statistics.group",
                version = 1,
                correlationId = context.CorrelationId,
                machine = group.Machine,
                project = group.Project,
                provider = group.Provider,
                model = group.Model,
                period = group.Period,
                measures = MeasuresRecord(group.Measures),
            });
        }

        return AltaExitCodes.Success;

        static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static async ValueTask<int> HandleFleetUpdateStatusAsync(AltaCommandContext context, string? server)
    {
        if (!context.TryGetRequired<IAltaFleet>(nameof(IAltaFleet), out var fleet)) return AltaExitCodes.ServiceUnavailable;
        var updates = await fleet.ReadUpdatesAsync(NormalizeOptionalText(server), context.CancellationToken).ConfigureAwait(false);
        if (updates.Status != "ok") return FleetFailure(context, updates.Status, updates.Message, null);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.fleet.updates",
            version = 1,
            correlationId = context.CorrelationId,
            server = updates.Server,
            serverVersion = updates.ServerVersion,
            policy = updates.Policy,
            pinnedVersion = updates.PinnedVersion,
            campaigns = updates.Campaigns.Count,
            note = "A person starts or cancels a campaign and sets the policy, in Settings > Fleet > Server > Updates.",
        });
        foreach (var campaign in updates.Campaigns)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.update.campaign",
                version = 1,
                correlationId = context.CorrelationId,
                id = campaign.Id,
                state = campaign.State,
                targetVersion = campaign.TargetVersion,
                createdAt = campaign.CreatedAt,
                automatic = campaign.Automatic,
                machines = campaign.Machines,
            });
        }

        foreach (var machine in updates.Machines)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.update.machine",
                version = 1,
                correlationId = context.CorrelationId,
                id = machine.Id,
                name = machine.Name,
                appVersion = machine.Version,
                online = machine.Online,
            });
        }

        return AltaExitCodes.Success;
    }

    private static int HandleFleetHealth(AltaCommandContext context)
    {
        if (!context.TryGetRequired<IAltaFleet>(nameof(IAltaFleet), out var fleet)) return AltaExitCodes.ServiceUnavailable;
        var servers = fleet.ListServers();
        foreach (var server in servers)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.fleet.server",
                version = 1,
                correlationId = context.CorrelationId,
                id = server.Id,
                url = server.Url,
                organization = server.Organization,
                connection = server.Connection,
                problem = server.Problem,
            });
        }

        var machines = fleet.ListMachines();
        var notReady = machines.Where(static machine => machine.Readiness != "ready").ToArray();
        foreach (var machine in notReady) AltaJsonlWriter.WriteRecord(context.Stdout, FleetMachineRecord(context, machine));
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.fleet.health",
            version = 1,
            correlationId = context.CorrelationId,
            servers = servers.Count,
            serversOnline = servers.Count(static server => server.Connection == "online"),
            machines = machines.Count,
            ready = machines.Count - notReady.Length,
            notReady = notReady.Length,
        });
        return AltaExitCodes.Success;
    }

    // The fleet is never a way out of a sandbox (rule 8): a session whose sandbox profile does not allow the fleet, or cannot
    // be used, runs no command of it, the reads included (SessionRuntimeService.GetFleetFloorAsync). A host without a
    // runtime has no sandboxed session.
    private static async ValueTask<int> WithFleetFloorAsync(AltaCommandContext context, Func<ValueTask<int>> run)
        => context.Services.Get<SessionRuntimeService>() is { } runtime && await RefuseFleetToCallerAsync(context, runtime).ConfigureAwait(false) is { } refused
            ? refused
            : await run().ConfigureAwait(false);

    // Moving another session is driving it: an agent moves only what asks the user at least as much as it does (a session
    // of this computer), or a child of its own on another machine, so that the fleet gives it no session it could not drive here.
    private static async Task<int?> RefuseMoveOfOtherAsync(AltaCommandContext context, string sessionId)
    {
        if (NormalizeOptionalText(context.Caller.SourceSessionId) is not { } callerId || IsCaller(context, sessionId)
            || context.Services.Get<SessionRuntimeService>() is not { } runtime)
            return null;
        if (await runtime.TryGetRecoverableSessionAsync(sessionId, context.CancellationToken).ConfigureAwait(false) is not null)
        {
            // The caller runs (attached, else taken to ask); the other session runs, once prompted, in the mode it records.
            var own = runtime.GetEffectivePermissionMode(callerId);
            var theirs = await runtime.GetPermissionModeAsync(sessionId, context.CancellationToken).ConfigureAwait(false);
            return SessionPermissionModes.Rank(theirs) > SessionPermissionModes.Rank(own)
                ? PermissionDenied(context, "session.moveDenied",
                    $"Session '{sessionId}' runs in '{theirs}', which asks the user less than this session ('{own}'): this session does not move it. Ask the user.")
                : null;
        }

        var report = runtime.RemoteChildren is { } remote ? await remote.ReportAsync(sessionId, context.CancellationToken).ConfigureAwait(false) : null;
        return report is not null && string.Equals(report.ParentSessionId, callerId, StringComparison.OrdinalIgnoreCase)
            ? null
            : PermissionDenied(context, "session.moveDenied",
                $"Session '{sessionId}' runs on another machine and is not a child of this session: this session does not move it. Ask the user.");
    }

    private static object FleetMachineRecord(AltaCommandContext context, AltaFleetMachine machine, AltaFleetUpdateState? update = null, string? note = null) => new
    {
        type = "alta.fleet.machine",
        version = 1,
        correlationId = context.CorrelationId,
        id = machine.Id,
        name = machine.Name,
        thisComputer = machine.IsThisComputer,
        state = machine.State,
        readiness = machine.Readiness,
        os = machine.Os,
        appVersion = machine.Version,
        capabilities = machine.Capabilities,
        labels = machine.Labels,
        providers = machine.Providers,
        permissions = machine.Permissions,
        sessions = machine.Sessions,
        working = machine.Working,
        paired = machine.Paired,
        channel = machine.Channel,
        lastSeen = machine.LastSeen,
        problem = machine.Problem,
        update = update is null ? null : new { running = update.Running, installed = update.Installed, pending = update.Pending, phase = update.Phase, lastOutcome = update.LastOutcome },
        note,
    };

    private static object MeasuresRecord(AltaFleetMeasures measures) => new
    {
        activeMs = measures.ActiveMs,
        runs = measures.Runs,
        runsFailed = measures.RunsFailed,
        requests = measures.Requests,
        inputTokens = measures.InputTokens,
        outputTokens = measures.OutputTokens,
        toolCalls = measures.ToolCalls,
        toolFailures = measures.ToolFailures,
        costs = measures.Costs,
    };

    // The refusal of the fleet as an error of the command: what is missing, what this computer may not do, what does not answer.
    private static int FleetFailure(AltaCommandContext context, string status, string? message, string? machine)
    {
        var (exitCode, code) = status switch
        {
            "not_found" or "machine_not_found" or "session_missing" => (AltaExitCodes.NotFound, "fleet.notFound"),
            "not_manager" or "not_allowed" or "forbidden" => (AltaExitCodes.PolicyDenied, "fleet.notAllowed"),
            "offline" or "connecting" or "unauthorized" or "machine_offline" or "unreachable" or "unavailable" => (AltaExitCodes.ServiceUnavailable, "fleet.unreachable"),
            "no_server" => (AltaExitCodes.Unsupported, "fleet.noServer"),
            _ => (AltaExitCodes.Failure, "fleet.refused"),
        };
        var what = status switch
        {
            "not_found" or "machine_not_found" => $"No machine is named '{machine}': `alta fleet machine list` lists them.",
            "not_manager" => $"This computer does not manage {machine ?? "the machine"}: its owner gives the `manage` permission in the page of the machine.",
            "no_server" => "This computer is linked to no CodeAlta Server: statistics and update campaigns are read from a server.",
            _ => null,
        };
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, exitCode, $"{what ?? $"{machine ?? "The fleet"} answered '{status}'."}{(message is null ? string.Empty : " " + message)}");
        return exitCode;
    }

    private static bool TryParseLimit(string? text, int defaultValue, int maximum, out int value)
    {
        value = defaultValue;
        return NormalizeOptionalText(text) is not { } given
            || int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= maximum;
    }

    private static bool TryParseDay(string? text, out DateOnly day, DateOnly defaultValue)
    {
        day = defaultValue;
        return NormalizeOptionalText(text) is not { } given || DateOnly.TryParseExact(given, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }
}
