using CodeAlta.Catalog;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta automation` commands: the automations of a host that has some (the desktop app).
internal sealed partial class BuiltInAltaCommandContributor
{
    /// <summary>The most runs one call lists.</summary>
    internal const int MaximumAutomationRuns = 100;

    private static readonly AltaCommandPolicy[] AutomationPolicies =
    [
        Read("automation list"),
        Read("automation show"),
        Read("automation current"),
        Read("automation runs"),
        Mutating("automation run"),
        Mutating("automation create"),
        Mutating("automation enable"),
        Mutating("automation disable"),
        Disruptive("automation delete"),
    ];

    private static Command CreateAutomationCommand(AltaCommandContext context)
    {
        var group = Group("automation", "Use the automations of CodeAlta: prompts that start a session on a schedule, on an event of the repository, when a command succeeds, or by hand.");
        group.Add(CreateAutomationListCommand(context));
        group.Add(CreateAutomationShowCommand(context));
        group.Add(CreateAutomationCurrentCommand(context));
        group.Add(CreateAutomationRunsCommand(context));
        group.Add(CreateAutomationRunCommand(context));
        group.Add(CreateAutomationCreateCommand(context));
        group.Add(CreateAutomationEnableCommand(context, enable: true));
        group.Add(CreateAutomationEnableCommand(context, enable: false));
        group.Add(CreateAutomationDeleteCommand(context));
        AddHelpText(
            group,
            "An automation has a name, a prompt and triggers; each run starts a new session in its project, or a chat, and sends it the prompt. It is kept in the configuration of the user or of its project, so it outlives this session, unlike a reminder.",
            "A session started by an automation finds it with `alta automation current`. Such a session reads the automations; it does not run, create, enable, disable or delete one.",
            "`allowed` is false for an automation that came with the repository of a project and that the user has not allowed yet in the Automations tab: its triggers start nothing until then. Only the user allows it.",
            "Triggers: `daily@09:00` (several times: `daily@09:00,17:30`), `hourly@15` (minute 15; every 2 hours: `hourly@15/2`), `weekly@mon,thu@08:30`, `cron@0 9 * * 1-5` (local time), `issue@opened`, `pull_request@opened`, `pull_request@updated` (new commits), `jira@created`, `jira@updated` (an issue of the Jira project of the project, when its configuration names one). No trigger: run it with `alta automation run`.",
            "A command trigger, `command@<command line>`, keeps a command running that waits for something, such as `command@gh run watch 123 --exit-status`: each time the command ends with the exit code 0 the automation starts a session, with what the command printed after the prompt, and the command is started again; another exit code starts nothing. Everything after `command@` is the command, run in the shell of `shell_command`, in the folder of the project (the home folder for a chat). A host that has the user review the commands of its sessions refuses it (`automation.commandDenied`): the user then creates it in the Automations tab.",
            "An issue or pull request trigger watches the repository of the project on GitHub, GitLab or Azure DevOps, and runs for what the people of the repository open (its owner, the members of its organization, its collaborators). Add `+anyone`, as in `issue@opened+anyone`, only when the user asks to run for every author: what a stranger writes then reaches a session.",
            "Examples: `alta automation list`; `alta automation create --name \"Nightly review\" --trigger daily@23:00 --content \"Review what changed today.\"`; `alta automation run <id>`; `alta automation runs <id>`.");
        return group;
    }

    private static Command CreateAutomationListCommand(AltaCommandContext context)
    {
        string? project = null;
        var chats = false;
        var command = Leaf("list", "List the automations: id, name, where each runs, its triggers, when it is next due and whether it runs now.");
        command.Add("project=", "Only the automations that run in this project: id, slug or path.", value => project = value);
        command.Add("chats", "Only the automations that run as a chat, in no project.", value => chats = value is not null);
        command.Add(async (_, _) => await HandleAutomationListAsync(context, project, chats).ConfigureAwait(false));
        return command;
    }

    private static Command CreateAutomationShowCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("show", "Show one automation with its prompt and its latest runs.");
        command.Add("<automation-id>", "Automation id from `alta automation list`.", value => id = value);
        command.Add((_, _) => ValueTask.FromResult(HandleAutomationShow(context, id)));
        return command;
    }

    private static Command CreateAutomationCurrentCommand(AltaCommandContext context)
    {
        string? session = null;
        var command = Leaf("current", "Show the automation that started a session, and the run that did.");
        command.Add("session=", "Session id. Defaults to the caller's current session.", value => session = value);
        command.Add((_, _) => ValueTask.FromResult(HandleAutomationCurrent(context, session)));
        AddHelpText(command, "The record `alta.automation.none` says the session was started by the user, or by another session. The `detail` of the run names the issue or the pull request that started it, or is the last line its command printed.");
        return command;
    }

    private static Command CreateAutomationRunsCommand(AltaCommandContext context)
    {
        string? id = null;
        string? limit = null;
        var command = Leaf("runs", "List the runs of an automation, newest first, each with the session it started.");
        command.Add("<automation-id>?", "Automation id. Without it, the runs of all the automations.", value => id = value);
        command.Add("limit=", $"The most runs to list, up to {MaximumAutomationRuns}. Defaults to 20.", value => limit = value);
        command.Add((_, _) => ValueTask.FromResult(HandleAutomationRuns(context, id, limit)));
        AddHelpText(command, "`status` is `running`, `completed`, `failed`, `cancelled`, `interrupted` (CodeAlta stopped during the run) or `skipped` (the previous run was still in progress). Read a session with `alta session show <session-id>`.");
        return command;
    }

    private static Command CreateAutomationRunCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("run", "Run an automation now, whatever its triggers: it starts a new session and sends it the prompt.");
        command.Add("<automation-id>", "Automation id from `alta automation list`.", value => id = value);
        command.Add(async (_, _) => await HandleAutomationRunAsync(context, id).ConfigureAwait(false));
        AddHelpText(command, "The command returns once the session exists: it does not wait for its answer. Follow it with `alta automation runs <id>`.");
        return command;
    }

    private static Command CreateAutomationCreateCommand(AltaCommandContext context)
    {
        var options = new AutomationCreateOptions();
        var command = Leaf("create", "Create an automation: a prompt that starts a session by itself.");
        command.Add("name=", "Name of the automation, which also names the sessions it starts. Required.", value => options.Name = value);
        command.Add("content=", "The prompt sent at each run. Prefer --stdin for multi-line content.", value => options.Content = value);
        command.Add("stdin", "Read the prompt from stdin.", value => options.UseStdin = value is not null);
        command.Add("trigger=", "A trigger; repeat the option for several. Without one the automation is run by hand.", value => { if (value is not null) options.Triggers.Add(value); });
        command.Add("project=", "The project it runs in: id, slug or path. Defaults to the caller's project.", value => options.Project = value);
        command.Add("chat", "Run as a chat, in no project.", value => options.Chat = value is not null);
        command.Add("store=", "Where it is written: `user` (default, the configuration of the user) or `project` (the .alta/config.toml of its project, shared with the repository).", value => options.Store = value);
        command.Add("model=", "Model ref `provider[:model][@effort]`. Defaults to the default provider and its first model.", value => options.Model = value);
        command.Add("agent=", "Agent prompt id. Defaults to the default one.", value => options.Agent = value);
        command.Add("disabled", "Create it with its triggers disabled.", value => options.Disabled = value is not null);
        command.Add("catch-up", "Run what was missed while CodeAlta was closed: once for a schedule, and for each issue or pull request of a trigger.", value => options.CatchUp = value is not null);
        command.Add(async (_, _) => await HandleAutomationCreateAsync(context, options).ConfigureAwait(false));
        AddHelpText(
            command,
            "Write the prompt for a session that starts with nothing else: say what to look at and what to produce.",
            "Examples: `alta automation create --name \"Issue triage\" --trigger daily@09:00 --content \"Triage the issues opened since yesterday.\"`; `--trigger issue@opened` to run for every issue a maintainer opens; `--trigger \"command@gh run watch 123 --exit-status\"` to run each time that command succeeds; `--chat` for one that needs no project.");
        return command;
    }

    private static Command CreateAutomationEnableCommand(AltaCommandContext context, bool enable)
    {
        string? id = null;
        var command = enable
            ? Leaf("enable", "Enable the triggers of an automation.")
            : Leaf("disable", "Disable the triggers of an automation. It can still be run with `alta automation run`.");
        command.Add("<automation-id>", "Automation id from `alta automation list`.", value => id = value);
        command.Add(async (_, _) => await HandleAutomationChangeAsync(context, id, enable ? "enable" : "disable",
            (automations, automation) => automations.SetEnabledAsync(automation, enable, context.CancellationToken)).ConfigureAwait(false));
        return command;
    }

    private static Command CreateAutomationDeleteCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("delete", "Remove an automation from its configuration file. Its runs and their sessions stay.");
        command.Add("<automation-id>", "Automation id from `alta automation list`.", value => id = value);
        command.Add(async (_, _) => await HandleAutomationChangeAsync(context, id, "delete",
            (automations, automation) => automations.DeleteAsync(automation, context.CancellationToken)).ConfigureAwait(false));
        AddHelpText(command, "Delete only the automations you created, or the ones the user asks you to remove.");
        return command;
    }

    private static bool TryGetAutomations(AltaCommandContext context, out IAltaAutomations automations)
    {
        if (context.Services.Get<IAltaAutomations>() is { } found)
        {
            automations = found;
            return true;
        }

        automations = null!;
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'IAltaAutomations' is unavailable.");
        return false;
    }

    // A session that an automation started read what its trigger gave it, which may be a stranger's words: it neither
    // runs nor changes automations, so that nothing it was told can keep itself going.
    private static bool IsStartedByAutomation(AltaCommandContext context, IAltaAutomations automations)
        => NormalizeOptionalText(context.Caller.SourceSessionId) is { } session && automations.FindRunOfSession(session) is not null;

    private static int AutomationCommandDenied(AltaCommandContext context, string? message)
        => PermissionDenied(context, "automation.commandDenied",
            message ?? "The user reviews the commands of this session: it cannot create or enable an automation that runs a command. The user creates it in the Automations tab.");

    // A trigger that keeps a command running, `command@<command line>`: the name before the first `@`, as the host reads it.
    private static bool IsCommandTrigger(string trigger)
        => trigger.IndexOf('@') is >= 0 and var at && trigger.AsSpan(0, at).Trim().Equals("command", StringComparison.OrdinalIgnoreCase);

    private static int AutomationSessionDenied(AltaCommandContext context)
        => PermissionDenied(context, "automation.startedByAutomation",
            "A session started by an automation does not run or change automations. Say what you would do: the user decides.");

    private static async ValueTask<int> HandleAutomationListAsync(AltaCommandContext context, string? projectRef, bool chats)
    {
        if (!TryGetAutomations(context, out var automations))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        string? projectId = null;
        if (NormalizeOptionalText(projectRef) is { } reference)
        {
            if (chats)
            {
                return UsageError(context, "usage.scopeConflict", "Use either --project or --chats, not both.", "alta automation list");
            }

            if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
            {
                return AltaExitCodes.ServiceUnavailable;
            }

            if (await ResolveProjectAsync(catalog, reference, context, includeArchived: true).ConfigureAwait(false) is not { } project)
            {
                return NotFound(context, "project.notFound", $"Project '{reference}' was not found.");
            }

            projectId = project.Id;
        }

        var listed = automations.List()
            .Where(automation => chats ? automation.ProjectId is null : projectId is null || string.Equals(automation.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var automation in listed)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, AutomationRecord(context, "alta.automation", automation, prompt: false));
        }

        WriteSummary(context, "alta.automation.summary", listed.Length, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int HandleAutomationShow(AltaCommandContext context, string? id)
    {
        if (!TryGetAutomations(context, out var automations))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } automationId)
        {
            return UsageError(context, "usage.missingAutomation", "An automation id is required.", "alta automation show");
        }

        if (FindAutomation(automations, automationId) is not { } automation)
        {
            return AutomationNotFound(context, automationId);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, AutomationRecord(context, "alta.automation", automation, prompt: true));
        foreach (var run in automations.ListRuns(automation.Id, 5))
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, AutomationRunRecord(context, run));
        }

        return AltaExitCodes.Success;
    }

    private static int HandleAutomationCurrent(AltaCommandContext context, string? session)
    {
        if (!TryGetAutomations(context, out var automations))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (FirstNonEmpty(session, context.Caller.SourceSessionId) is not { } sessionId)
        {
            return UsageError(context, "usage.missingSession", "A session id is required outside a session caller. Use --session <session-id>.", "alta automation current");
        }

        if (automations.FindRunOfSession(sessionId) is not { } run)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new { type = "alta.automation.none", version = 1, correlationId = context.CorrelationId, sessionId });
            return AltaExitCodes.Success;
        }

        // The automation may have been removed since: the run still says which one it was.
        if (FindAutomation(automations, run.AutomationId) is { } automation)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, AutomationRecord(context, "alta.automation", automation, prompt: true));
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, AutomationRunRecord(context, run));
        return AltaExitCodes.Success;
    }

    private static int HandleAutomationRuns(AltaCommandContext context, string? id, string? limitText)
    {
        if (!TryGetAutomations(context, out var automations))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var limit = 20;
        if (NormalizeOptionalText(limitText) is { } text && (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > MaximumAutomationRuns))
        {
            return UsageError(context, "usage.invalidLimit", $"--limit is a number from 1 to {MaximumAutomationRuns}.", "alta automation runs");
        }

        string? automationId = null;
        if (NormalizeOptionalText(id) is { } reference)
        {
            // The runs of an automation that was removed can still be listed by its id.
            automationId = FindAutomation(automations, reference)?.Id ?? reference.ToLowerInvariant();
        }

        var runs = automations.ListRuns(automationId, limit);
        foreach (var run in runs)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, AutomationRunRecord(context, run));
        }

        WriteSummary(context, "alta.automation.run.summary", runs.Count, truncated: runs.Count == limit);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleAutomationRunAsync(AltaCommandContext context, string? id)
    {
        if (!TryGetAutomations(context, out var automations))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (IsStartedByAutomation(context, automations))
        {
            return AutomationSessionDenied(context);
        }

        if (NormalizeOptionalText(id) is not { } reference)
        {
            return UsageError(context, "usage.missingAutomation", "An automation id is required.", "alta automation run");
        }

        if (FindAutomation(automations, reference) is not { } automation || await automations.RunAsync(automation.Id, context.CancellationToken).ConfigureAwait(false) is not { } run)
        {
            return AutomationNotFound(context, reference);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, AutomationRunRecord(context, run));
        if (run.Status != "failed")
        {
            return AltaExitCodes.Success;
        }

        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "automation.runFailed", AltaExitCodes.Failure, run.Message ?? "The automation did not start.");
        return AltaExitCodes.Failure;
    }

    private static async ValueTask<int> HandleAutomationCreateAsync(AltaCommandContext context, AutomationCreateOptions options)
    {
        if (!TryGetAutomations(context, out var automations))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (IsStartedByAutomation(context, automations))
        {
            return AutomationSessionDenied(context);
        }

        if (NormalizeOptionalText(options.Name) is not { } name)
        {
            return UsageError(context, "usage.missingName", "A name is required. Use --name <text>.", "alta automation create");
        }

        if (!string.IsNullOrWhiteSpace(options.Content) && options.UseStdin)
        {
            return UsageError(context, "usage.contentConflict", "Use either --content or --stdin, not both.", "alta automation create");
        }

        var prompt = options.UseStdin ? await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false) : options.Content;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return UsageError(context, "usage.missingContent", "The prompt is required. Use --content <text> or --stdin.", "alta automation create");
        }

        var store = NormalizeOptionalText(options.Store)?.ToLowerInvariant() ?? "user";
        if (store is not ("user" or "project"))
        {
            return UsageError(context, "usage.invalidStore", "--store is `user` or `project`.", "alta automation create");
        }

        string? projectId = null;
        if (!options.Chat)
        {
            var reference = NormalizeOptionalText(options.Project) ?? NormalizeOptionalText(context.Caller.SourceProjectId);
            if (reference is not null)
            {
                if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
                {
                    return AltaExitCodes.ServiceUnavailable;
                }

                if (await ResolveProjectAsync(catalog, reference, context, includeArchived: false).ConfigureAwait(false) is not { } project)
                {
                    return NotFound(context, "project.notFound", $"Project '{reference}' was not found.");
                }

                projectId = project.Id;
            }
        }
        else if (NormalizeOptionalText(options.Project) is not null)
        {
            return UsageError(context, "usage.scopeConflict", "Use either --project or --chat, not both.", "alta automation create");
        }

        if (store == "project" && projectId is null)
        {
            return UsageError(context, "usage.invalidStore", "`--store project` needs a project: a chat is kept in the configuration of the user.", "alta automation create");
        }

        string[] triggers = [.. options.Triggers.Select(static trigger => trigger.Trim()).Where(static trigger => trigger.Length > 0)];
        // The command of a trigger is one nobody reviews.
        if (triggers.Any(IsCommandTrigger) && ReviewsCommands(context))
        {
            return AutomationCommandDenied(context, null);
        }

        var change = await automations.CreateAsync(new AltaAutomationRequest(name, prompt)
        {
            ProjectId = projectId,
            StoreInProject = store == "project",
            Model = NormalizeOptionalText(options.Model),
            Agent = NormalizeOptionalText(options.Agent),
            Enabled = !options.Disabled,
            CatchUp = options.CatchUp,
            Triggers = triggers,
        }, context.CancellationToken).ConfigureAwait(false);
        if (change.Status == "denied")
        {
            return AutomationCommandDenied(context, change.Message);
        }

        if (change.Status != "ok" || change.Id is null || FindAutomation(automations, change.Id) is not { } created)
        {
            return UsageError(context, "automation.refused", change.Message ?? "The automation was not created.", "alta automation create");
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, AutomationRecord(context, "alta.automation.created", created, prompt: false));
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleAutomationChangeAsync(AltaCommandContext context, string? id, string verb,
        Func<IAltaAutomations, string, Task<AltaAutomationChange>> change)
    {
        if (!TryGetAutomations(context, out var automations))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (IsStartedByAutomation(context, automations))
        {
            return AutomationSessionDenied(context);
        }

        if (NormalizeOptionalText(id) is not { } reference)
        {
            return UsageError(context, "usage.missingAutomation", "An automation id is required.", "alta automation " + verb);
        }

        if (FindAutomation(automations, reference) is not { } automation)
        {
            return AutomationNotFound(context, reference);
        }

        // Enabling it starts its command; disabling it, or removing it, only ends one.
        if (verb == "enable" && automation.Triggers.Any(IsCommandTrigger) && ReviewsCommands(context))
        {
            return AutomationCommandDenied(context, null);
        }

        var result = await change(automations, automation.Id).ConfigureAwait(false);
        if (result.Status == "not_found")
        {
            return AutomationNotFound(context, reference);
        }

        if (result.Status == "denied")
        {
            return AutomationCommandDenied(context, result.Message);
        }

        if (result.Status != "ok")
        {
            AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "automation.refused", AltaExitCodes.Failure, result.Message ?? "The automation was not changed.");
            return AltaExitCodes.Failure;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = verb == "delete" ? "alta.automation.deleted" : "alta.automation.changed",
            version = 1,
            correlationId = context.CorrelationId,
            id = automation.Id,
            name = automation.Name,
            enabled = verb == "delete" ? (bool?)null : verb == "enable",
        });
        return AltaExitCodes.Success;
    }

    // By its id, or by the start of it when that names one automation only.
    private static AltaAutomation? FindAutomation(IAltaAutomations automations, string reference)
    {
        var all = automations.List();
        if (all.FirstOrDefault(automation => string.Equals(automation.Id, reference, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
        var matches = reference.Length < 4 ? [] : all.Where(automation => automation.Id.StartsWith(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static int AutomationNotFound(AltaCommandContext context, string id)
        => NotFound(context, "automation.notFound", $"Automation '{id}' was not found. List the automations with `alta automation list`.");

    private static object AutomationRecord(AltaCommandContext context, string type, AltaAutomation automation, bool prompt) => new
    {
        type,
        version = 1,
        correlationId = context.CorrelationId,
        id = automation.Id,
        name = automation.Name,
        enabled = automation.Enabled,
        runsIn = automation.ProjectId is null ? "chat" : "project",
        projectId = automation.ProjectId,
        projectPath = automation.ProjectPath,
        triggers = automation.Triggers,
        model = automation.Model,
        agent = automation.Agent,
        nextRunAt = automation.NextRunAt,
        running = automation.Running,
        allowed = automation.Allowed,
        problem = automation.Problem,
        file = automation.File,
        prompt = prompt ? automation.Prompt : null,
    };

    private static object AutomationRunRecord(AltaCommandContext context, AltaAutomationRun run) => new
    {
        type = "alta.automation.run",
        version = 1,
        correlationId = context.CorrelationId,
        id = run.Id,
        automationId = run.AutomationId,
        name = run.Name,
        status = run.Status,
        trigger = run.Trigger,
        detail = run.Detail,
        sessionId = run.SessionId,
        projectId = run.ProjectId,
        startedAt = run.StartedAt,
        endedAt = run.EndedAt,
        message = run.Message,
    };

    private sealed class AutomationCreateOptions
    {
        public string? Name { get; set; }

        public string? Content { get; set; }

        public bool UseStdin { get; set; }

        public List<string> Triggers { get; } = [];

        public string? Project { get; set; }

        public bool Chat { get; set; }

        public string? Store { get; set; }

        public string? Model { get; set; }

        public string? Agent { get; set; }

        public bool Disabled { get; set; }

        public bool CatchUp { get; set; }
    }
}
