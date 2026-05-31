using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using CodeAlta.Plugins;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

internal sealed class BuiltInAltaCommandContributor : IAltaCommandContributor
{
    // Agent-originated sends should return after the delegated run is accepted, not after
    // the delegated LLM finishes. The timeout is only a submission-acknowledgement guard.
    private static readonly TimeSpan AgentCallerSubmitAckTimeout = TimeSpan.FromSeconds(5);
    private const string NotificationFollowUpNextStep = "Do not call any tool, shell sleep, reminder, status, tail, events, or polling command to wait for completion; yield control and wait for parent-session notifications.";
    private const string NotificationFollowUpGuidance = "Do not poll or actively wait for this delegated session to complete. CodeAlta will forward the delegated session's final assistant reply to the parent session automatically.";

    private static readonly string[] NotificationFollowUpForbiddenWaitActions =
    [
        "tool call",
        "shell sleep",
        "reminder",
        "session status",
        "session tail",
        "session events",
        "polling",
    ];

    private static readonly AltaCommandPolicy[] Policies =
    [
        Read("version", supportsCatalogOnlyContext: true),
        Read("project list", supportsCatalogOnlyContext: true),
        Read("project show", supportsCatalogOnlyContext: true),
        Read("project resolve", supportsCatalogOnlyContext: true),
        Read("project current", supportsCatalogOnlyContext: true),
        Mutating("project upsert", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Read("session list"),
        Read("session show"),
        Read("session status"),
        Read("session result"),
        Read("session report"),
        Read("session children"),
        Read("session model"),
        Read("session metrics"),
        Read("session tail"),
        Read("session events"),
        Mutating("session create"),
        Mutating("session send"),
        Mutating("session steer"),
        Mutating("session queue"),
        Disruptive("session abort"),
        Mutating("session compact"),
        Read("session join"),
        Mutating("session message"),
        Mutating("session request"),
        Mutating("reminder create"),
        Read("reminder list"),
        Mutating("reminder delete"),
        Read("skill list", supportsCatalogOnlyContext: true),
        Read("skill show", supportsCatalogOnlyContext: true),
        Mutating("skill activate"),
        Mutating("skills activate"),
        Mutating("skills_activate"),
        Read("tool status", supportsCatalogOnlyContext: true),
        Read("tool list", supportsCatalogOnlyContext: true),
        Read("tool capability list", supportsCatalogOnlyContext: true),
        Read("provider list"),
        Read("provider model list"),
        Read("model list"),
        Read("model show"),
        Read("model resolve"),
        Read("prompt list", supportsCatalogOnlyContext: true),
        Read("prompt show", supportsCatalogOnlyContext: true),
        Mutating("prompt create", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Mutating("prompt edit", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Read("plugin list"),
        Read("plugin status"),
    ];

    public IEnumerable<CommandNode> CreateCommandLineNodes(AltaCommandContributionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        yield return CreateVersionCommand(context.Invocation);
        yield return CreateProjectCommand(context.Invocation);
        yield return CreateSessionCommand(context.Invocation);
        yield return CreateReminderCommand(context.Invocation);
        yield return CreateSkillCommand(context.Invocation);
        yield return CreateSkillsAliasCommand(context.Invocation);
        yield return CreateSkillsActivateAliasCommand(context.Invocation);
        yield return CreateToolCommand(context.Invocation);
        yield return CreateProviderCommand(context.Invocation);
        yield return CreateModelCommand(context.Invocation);
        yield return CreatePromptCommand(context.Invocation);
        yield return CreatePluginCommand(context.Invocation);
    }

    public IEnumerable<AltaCommandPolicy> GetCommandPolicies(AltaCommandContributionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Policies;
    }

    private static IReadOnlyList<AltaCommandPolicy> GetEffectivePolicies(AltaCommandContext context)
    {
        if (context.Services.Get<AltaCommandRegistry>() is { } registry)
        {
            return registry.GetPolicies(context);
        }

        if (context.Services.Get<IAltaPluginCatalog>() is { } catalog)
        {
            return Policies.Concat(catalog.ListCommandPolicies())
                .OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return Policies;
    }

    private static AltaCommandPolicy Read(string path, bool requiresRuntime = true, bool supportsCatalogOnlyContext = false)
        => new()
        {
            Path = path,
            RequiresInProcessRuntime = requiresRuntime,
            SupportsCatalogOnlyContext = supportsCatalogOnlyContext,
        };

    private static AltaCommandPolicy Mutating(string path, bool requiresRuntime = true, bool supportsCatalogOnlyContext = false)
        => new()
        {
            Path = path,
            RequiresInProcessRuntime = requiresRuntime,
            SupportsCatalogOnlyContext = supportsCatalogOnlyContext,
            IsMutating = true,
        };

    private static AltaCommandPolicy Disruptive(string path)
        => new()
        {
            Path = path,
            RequiresInProcessRuntime = true,
            IsMutating = true,
            IsDisruptive = true,
        };

    private static Command CreateVersionCommand(AltaCommandContext context)
    {
        var command = Leaf("version", "Print the CodeAlta/alta live-tool version as JSONL.");
        command.Add((_, _) =>
        {
            var assembly = typeof(BuiltInAltaCommandContributor).Assembly;
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.version",
                version = 1,
                correlationId = context.CorrelationId,
                product = "CodeAlta",
                liveToolAssembly = assembly.GetName().Name,
                informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString(),
            });
            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static Command CreateProjectCommand(AltaCommandContext context)
    {
        var group = Group("project", "Inspect and manage project catalog entries.");
        group.Add(CreateProjectListCommand(context));
        group.Add(CreateProjectShowCommand(context));
        group.Add(CreateProjectResolveCommand(context));
        group.Add(CreateProjectCurrentCommand(context));
        group.Add(CreateProjectUpsertCommand(context));
        AddHelpText(
            group,
            "Examples: `alta project current`; `alta project list`; `alta project show CodeAlta`; `alta project resolve --path C:/code/CodeAlta`.");
        return group;
    }

    private static Command CreateProjectListCommand(AltaCommandContext context)
    {
        var includeArchived = false;
        var detailed = false;
        var command = Leaf("list", "List known projects from the CodeAlta catalog.");
        command.Add("include-archived", "Include archived projects.", value => includeArchived = value is not null);
        command.Add("detailed", "Emit one detailed metadata record per project instead of the compact project refs array.", value => detailed = value is not null);
        command.Add(async (_, _) => await HandleProjectListAsync(context, includeArchived, detailed).ConfigureAwait(false));
        return command;
    }

    private static Command CreateProjectShowCommand(AltaCommandContext context)
    {
        string? reference = null;
        var command = Leaf("show", "Show one project by id, slug, or path.");
        command.Add("<project>", "Project id, slug, or path.", value => reference = value);
        command.Add(async (_, _) => await HandleProjectShowAsync(context, reference).ConfigureAwait(false));
        return command;
    }

    private static Command CreateProjectResolveCommand(AltaCommandContext context)
    {
        string? path = null;
        var command = Leaf("resolve", "Resolve the current directory or a supplied path to a catalog project.");
        command.Add("path=", "Project path to resolve. Defaults to cwd/current directory.", value => path = value);
        command.Add(async (_, _) => await HandleProjectResolveAsync(context, path).ConfigureAwait(false));
        return command;
    }

    private static Command CreateProjectCurrentCommand(AltaCommandContext context)
    {
        var command = Leaf("current", "Resolve the invocation cwd/current directory to a catalog project.");
        command.Add(async (_, _) => await HandleProjectResolveAsync(context, null).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta project current` returns the catalog project matched by the live-tool cwd.");
        return command;
    }

    private static Command CreateProjectUpsertCommand(AltaCommandContext context)
    {
        string? path = null;
        var command = Leaf("upsert", "Ensure a local path exists as a project catalog entry.");
        command.Add("<path>", "Local project path.", value => path = value);
        command.Add(async (_, _) => await HandleProjectUpsertAsync(context, path).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionCommand(AltaCommandContext context)
    {
        var group = Group("session", "Inspect, create, and control CodeAlta work sessions.");
        group.Add(CreateSessionListCommand(context));
        group.Add(CreateSessionShowCommand(context));
        group.Add(CreateSessionStatusCommand(context));
        group.Add(CreateSessionResultCommand(context));
        group.Add(CreateSessionReportCommand(context));
        group.Add(CreateSessionChildrenCommand(context));
        group.Add(CreateSessionModelCommand(context));
        group.Add(CreateSessionMetricsCommand(context));
        group.Add(CreateSessionTailCommand(context));
        group.Add(CreateSessionEventsCommand(context));
        group.Add(CreateSessionCreateCommand(context));
        group.Add(CreateSessionSendCommand(context));
        group.Add(CreateSessionSteerCommand(context));
        group.Add(CreateSessionQueueCommand(context));
        group.Add(CreateSessionAbortCommand(context));
        group.Add(CreateSessionCompactCommand(context));
        group.Add(CreateSessionJoinCommand(context));
        group.Add(CreateSessionMessageCommand(context));
        group.Add(CreateSessionRequestCommand(context));
        AddHelpText(
            group,
            "Common: list by project, create child sessions, send prompts, inspect result/metrics snapshots, and steer active runs.",
            "Examples:",
            "  `alta session list --project CodeAlta --state all --limit 20`",
            "  `alta session create --project CodeAlta --same-model-as <session-id>`",
            "  `alta session send <session-id> --stdin`",
            "  `alta session result <session-id>`",
            "  `alta session report <session-id-1> <session-id-2> --include=result,metrics`");
        return group;
    }

    private static Command CreateSessionListCommand(AltaCommandContext context)
    {
        string? project = null;
        string? state = null;
        string? provider = null;
        var limit = 50;
        var includeMetrics = false;
        var command = Leaf("list", "List recoverable/live sessions as JSONL.");
        command.Add("project=", "Filter by project id, slug, or path.", value => project = value);
        command.Add("state=", "Filter by running, idle, inactive, archived, or all.", value => state = ValidateState(value));
        command.Add("provider=", "Filter by provider id.", value => provider = value);
        command.Add("backend=", "Compatibility alias for --provider.", value => provider = value);
        command.Add("limit=", "Maximum sessions to return.", (int value) => limit = value);
        command.Add("metrics", "Include compact session metrics for emitted rows. This reads stored session history.", value => includeMetrics = value is not null);
        command.Add(async (_, _) => await HandleSessionListAsync(context, project, state, provider, limit, includeMetrics).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session list --project CodeAlta --state all --limit 20`; add `--metrics` for compact duration/usage fields.");
        return command;
    }

    private static Command CreateSessionShowCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var command = Leaf("show", "Show one session descriptor and live status.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add(async (_, _) => await HandleSessionShowAsync(context, sessionId, "alta.session.detail").ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionStatusCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var command = Leaf("status", "Show compact live status for one session.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add(async (_, _) => await HandleSessionShowAsync(context, sessionId, "alta.session.status").ConfigureAwait(false));
        AddHelpText(command, "Example: `alta session status <session-id>` after choosing a session from `alta session list`.");
        return command;
    }

    private static Command CreateSessionResultCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var scope = "last-turn";
        var command = Leaf("result", "Return one session's final answer or error with compact metrics.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("scope=", "Result scope: last-turn or session. Defaults to last-turn.", value => scope = value ?? scope);
        command.Add(async (_, _) => await HandleSessionResultAsync(context, sessionId, scope).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta session result <session-id>` after a completion notification or for diagnostics.");
        return command;
    }

    private static Command CreateSessionReportCommand(AltaCommandContext context)
    {
        var sessionIds = new List<string>();
        var scope = "last-turn";
        var include = "result,metrics";
        var useStdin = false;
        var command = Leaf("report", "Aggregate result/metric snapshots for multiple sessions.");
        command.Add("<session-id>*", "Session ids to report.", value =>
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                sessionIds.Add(value);
            }
        });
        command.Add("stdin", "Read additional session ids from stdin, separated by whitespace, commas, or newlines.", value => useStdin = value is not null);
        command.Add("scope=", "Result scope: last-turn or session. Defaults to last-turn.", value => scope = value ?? scope);
        command.Add("include=", "Comma-separated sections: result,metrics. Defaults to result,metrics.", value => include = value ?? include);
        command.Add(async (_, _) => await HandleSessionReportAsync(context, sessionIds, useStdin, scope, include).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session report <session-id-1> <session-id-2> --include=result,metrics`; `alta session report --stdin --include=result,metrics`.");
        return command;
    }

    private static Command CreateSessionChildrenCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var recursive = false;
        var command = Leaf("children", "List child sessions for a parent session.");
        command.Add("<session-id>", "Parent session id.", value => sessionId = value);
        command.Add("recursive", "Include descendants recursively.", value => recursive = value is not null);
        command.Add(async (_, _) => await HandleSessionChildrenAsync(context, sessionId, recursive).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionModelCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var command = Leaf("model", "Show the provider/model/reasoning selection for one session.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add(async (_, _) => await HandleSessionModelAsync(context, sessionId).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionMetricsCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var scope = "last-turn";
        var command = Leaf("metrics", "Summarize session timing, answer, tool, and usage metrics.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("scope=", "Metric scope: last-turn or session. Defaults to last-turn.", value => scope = value ?? scope);
        command.Add(async (_, _) => await HandleSessionMetricsAsync(context, sessionId, scope).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session metrics <session-id>`; `alta session metrics <session-id> --scope session`.");
        return command;
    }

    private static Command CreateSessionTailCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var last = 20;
        var options = new SessionEventsOptions();
        var command = Leaf("tail", "Return a finite sanitized snapshot of recent session events.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("last=", "Number of recent events to return.", (int value) => last = value);
        AddSessionEventOptions(command, options);
        command.Add(async (_, _) => await HandleSessionEventsAsync(context, sessionId, since: null, limit: last, options, fromTail: true).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session tail <session-id> --last 10`; add `--include user,assistant` or `--kind assistant.message` to narrow output.");
        return command;
    }

    private static Command CreateSessionEventsCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        long? since = null;
        var limit = 50;
        var options = new SessionEventsOptions();
        var command = Leaf("events", "Return a finite sanitized snapshot of session events.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("since=", "Return events after this sequence number.", (long value) => since = value);
        command.Add("limit=", "Maximum events to return.", (int value) => limit = value);
        AddSessionEventOptions(command, options);
        command.Add(async (_, _) => await HandleSessionEventsAsync(context, sessionId, since, limit, options, fromTail: false).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session events <session-id> --since 120 --limit 50 --include assistant,tool`; `alta session events <session-id> --kind assistant.message --fields timestamp,kind,content`.");
        return command;
    }

    private static void AddSessionEventOptions(Command command, SessionEventsOptions options)
    {
        command.Add("include=", "Comma-separated categories: user,assistant,reasoning,tool,host,event.", value => options.Include = value);
        command.Add("kind=", "Comma-separated exact event kinds, e.g. assistant.message or tool.output.", value => options.Kind = value);
        command.Add("fields=", "Comma-separated output fields. Always includes type/version/correlationId. Supported: sessionId,sequenceNumber,timestamp,kind,role,source,content,text,metadata.", value => options.Fields = value);
        command.Add("no-tool-output", "Suppress tool output content records while keeping non-output events.", value => options.NoToolOutput = value is not null);
    }

    private static Command CreateSessionCreateCommand(AltaCommandContext context)
    {
        var options = new SessionCreateOptions();
        var command = Leaf("create", "Create a project or global session using resolved model selection.");
        AddModelSelectionOptions(command, options.Model);
        command.Add("project=", "Target project id, slug, or path.", value => options.Project = value);
        command.Add("global", "Create a global coordinator session.", value => options.Global = value is not null);
        command.Add("title=", "Session title.", value => options.Title = value);
        command.Add("parent=", "Explicit parent session id for lineage.", value => options.ParentSessionId = value);
        command.Add("no-parent", "Suppress automatic parent assignment.", value => options.NoParent = value is not null);
        command.Add(async (_, _) => await HandleSessionCreateAsync(context, options).ConfigureAwait(false));
        AddHelpText(
            command,
            "Examples:",
            "  `alta session create --project CodeAlta --reasoning low`",
            "  `alta session create --project CodeAlta --same-model-as <session-id>`",
            "  `alta session create --global --model-ref codex:gpt-5.5@high`");
        return command;
    }

    private static Command CreateSessionSendCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var options = new PromptOptions();
        var command = Leaf("send", "Submit a normal prompt to a session and return run metadata.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        AddMessageOptions(command, options);
        command.Add("prompt-id=", "User prompt id for this send/session, as shown by `alta prompt list`. Defaults unchanged when omitted.", value => options.PromptId = value);
        command.Add("queue-if-busy", "Queue instead of failing when the target is busy, if a queue service is available.", value => options.QueueIfBusy = value is not null);
        command.Add(async (_, _) => await HandleSessionSendAsync(context, sessionId, options, PromptDispatchKind.Send).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta session send <session-id> --message \"Summarize status\"`; prefer `--stdin` for multi-line prompts.", "Use `--prompt-id <id>` to select a user prompt from `alta prompt list`; omit it to keep the current/default prompt selection.");
        return command;
    }

    private static Command CreateSessionSteerCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var options = new PromptOptions();
        var command = Leaf("steer", "Send steering input to an active run.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        AddMessageOptions(command, options);
        command.Add(async (_, _) => await HandleSessionSendAsync(context, sessionId, options, PromptDispatchKind.Steer).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta session steer <session-id> --message \"Please focus on tests first\"`; use only while the target is running.");
        return command;
    }

    private static Command CreateSessionQueueCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var options = new PromptOptions();
        var command = Leaf("queue", "Queue a prompt for later session submission when a queue service is available.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        AddMessageOptions(command, options);
        command.Add(async (_, _) => await HandleSessionSendAsync(context, sessionId, options, PromptDispatchKind.Queue).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionAbortCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        string? reason = null;
        var command = Leaf("abort", "Abort active work in a session.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("reason=", "Abort reason for diagnostics.", value => reason = value);
        command.Add(async (_, _) => await HandleSessionAbortAsync(context, sessionId, reason).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionCompactCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var submit = false;
        var command = Leaf("compact", "Request manual compaction for a session.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("submit", "Reserved for submit-after-compaction behavior.", value => submit = value is not null);
        command.Add(async (_, _) => await HandleSessionCompactAsync(context, sessionId, submit).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionJoinCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var command = Leaf("join", "Return non-blocking context needed to address a target session later.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add(async (_, _) => await HandleSessionShowAsync(context, sessionId, "alta.session.join").ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionMessageCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var options = new PromptOptions { MessageKind = "note" };
        var command = Leaf("message", "Send an attributed peer-agent message to a session.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        AddMessageOptions(command, options);
        command.Add("kind=", "Message kind: note, request, handoff, or answer.", value => options.MessageKind = ValidateMessageKind(value));
        command.Add(async (_, _) => await HandleSessionSendAsync(context, sessionId, options, PromptDispatchKind.Message).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionRequestCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var options = new PromptOptions { MessageKind = "request" };
        var command = Leaf("request", "Send an attributed peer-agent request to a session.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        AddMessageOptions(command, options);
        command.Add("reply-requested", "Annotate the request as expecting a reply.", value => options.ReplyRequested = value is not null);
        command.Add(async (_, _) => await HandleSessionSendAsync(context, sessionId, options, PromptDispatchKind.Request).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta session request <session-id> --reply-requested --stdin` for coordinator-to-agent requests.");
        return command;
    }

    private static Command CreateReminderCommand(AltaCommandContext context)
    {
        var group = Group("reminder", "Create, list, and delete delayed prompt reminders.");
        group.Add(CreateReminderCreateCommand(context));
        group.Add(CreateReminderListCommand(context));
        group.Add(CreateReminderDeleteCommand(context));
        AddHelpText(
            group,
            "Examples: `alta reminder create --duration 60 --content \"Check status\"`; `alta reminder create --duration 300 --repeat 3 --session <session-id> --stdin`; `alta reminder list --all`; `alta reminder delete <reminder-id>`.");
        return group;
    }

    private static Command CreateReminderCreateCommand(AltaCommandContext context)
    {
        var options = new ReminderCreateOptions();
        var command = Leaf("create", "Schedule prompt content to be sent to a session later.");
        command.Add("duration=", "Delay before each firing, in seconds; TimeSpan values such as 00:01:00 are also accepted.", value => options.Duration = value);
        command.Add("content=", "Prompt content to send when the reminder fires. Prefer --stdin for multi-line content.", value => options.Content = value);
        command.Add("stdin", "Read reminder prompt content from stdin.", value => options.UseStdin = value is not null);
        command.Add("session=", "Target CodeAlta session id. Defaults to the caller's current session when available.", value => options.SessionId = value);
        command.Add("session-id=", "Alias for --session.", value => options.SessionId = value);
        command.Add("repeat=", "Number of total firings. Defaults to 1.", value => options.Repeat = value);
        command.Add(async (_, _) => await HandleReminderCreateAsync(context, options).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta reminder create --duration 60 --content \"Remind me to check tests\"`; use `--session <session-id>` to target another session and `--repeat 10` to fire ten times.");
        return command;
    }

    private static Command CreateReminderListCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var includeCompleted = false;
        var command = Leaf("list", "List scheduled prompt reminders.");
        command.Add("session=", "Filter by target session id.", value => sessionId = value);
        command.Add("session-id=", "Alias for --session.", value => sessionId = value);
        command.Add("all", "Include completed reminders as well as active reminders.", value => includeCompleted = value is not null);
        command.Add((_, _) => ValueTask.FromResult(HandleReminderList(context, sessionId, includeCompleted)));
        return command;
    }

    private static Command CreateReminderDeleteCommand(AltaCommandContext context)
    {
        string? reminderId = null;
        var command = Leaf("delete", "Delete a scheduled prompt reminder.");
        command.Add("<reminder-id>", "Reminder id returned by `alta reminder create` or `alta reminder list`.", value => reminderId = value);
        command.Add((_, _) => ValueTask.FromResult(HandleReminderDelete(context, reminderId)));
        return command;
    }

    private static Command CreateSkillCommand(AltaCommandContext context)
    {
        var group = Group("skill", "Discover and activate CodeAlta-managed skills.");
        group.Add(CreateSkillListCommand(context));
        group.Add(CreateSkillShowCommand(context));
        group.Add(CreateSkillActivateCommand(context, "activate"));
        AddHelpText(group, "Examples: `alta skill list`; `alta skill show <skill-name>`; `alta skill activate <skill-name> --session <session-id>`.");
        return group;
    }

    private static Command CreateSkillsAliasCommand(AltaCommandContext context)
    {
        var group = Group("skills", "Compatibility alias for `skill`.");
        group.Add(CreateSkillActivateCommand(context, "activate"));
        return group;
    }

    private static Command CreateSkillsActivateAliasCommand(AltaCommandContext context)
    {
        return CreateSkillActivateCommand(context, "skills_activate");
    }

    private static Command CreateSkillListCommand(AltaCommandContext context)
    {
        string? project = null;
        var detailed = false;
        var command = Leaf("list", "List discovered CodeAlta skills.");
        command.Add("project=", "Project id, slug, or path for project-local skill roots.", value => project = value);
        command.Add("detailed", "Emit one detailed metadata record per skill instead of compact skill refs.", value => detailed = value is not null);
        command.Add(async (_, _) => await HandleSkillListAsync(context, project, detailed).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSkillShowCommand(AltaCommandContext context)
    {
        string? skillName = null;
        string? project = null;
        var command = Leaf("show", "Show one skill descriptor and content metadata.");
        command.Add("<skill-name>", "Skill name.", value => skillName = value);
        command.Add("project=", "Project id, slug, or path for project-local skill roots.", value => project = value);
        command.Add(async (_, _) => await HandleSkillShowAsync(context, skillName, project).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSkillActivateCommand(AltaCommandContext context, string name)
    {
        string? skillName = null;
        string? sessionId = null;
        var command = Leaf(name, name == "skills_activate" ? "Compatibility skill activation command." : "Activate a CodeAlta-managed skill for a session.");
        command.Add("<skill-name>", "Skill name.", value => skillName = value);
        command.Add("session=", "Target session/session id.", value => sessionId = value);
        command.Add(async (_, _) => await HandleSkillActivateAsync(context, skillName, sessionId).ConfigureAwait(false));
        return command;
    }

    private static Command CreateToolCommand(AltaCommandContext context)
    {
        var group = Group("tool", "Inspect alta live-tool status, policies, and capabilities.");
        var capability = Group("capability", "Inspect alta capability metadata.");
        capability.Add(CreateToolCapabilityListCommand(context));
        group.Add(CreateToolStatusCommand(context));
        group.Add(CreateToolListCommand(context));
        group.Add(capability);
        AddHelpText(group, "Examples: `alta tool status`; `alta tool capability list` to see host/runtime/provider/plugin availability as JSONL.");
        return group;
    }

    private static Command CreateToolStatusCommand(AltaCommandContext context)
    {
        var command = Leaf("status", "Show live-tool availability and caller/output context.");
        command.Add((_, _) =>
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.tool.status",
                version = 1,
                correlationId = context.CorrelationId,
                available = true,
                inProcess = true,
                caller = context.Caller,
                cwd = context.Cwd,
                maxOutputRecords = context.MaxOutputRecords,
                maxOutputBytes = context.MaxOutputBytes,
            });
            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static Command CreateToolListCommand(AltaCommandContext context)
    {
        var detailed = false;
        var command = Leaf("list", "List built-in alta command policy entries.");
        command.Add("detailed", "Emit one detailed policy record per command instead of the compact paths/classification record.", value => detailed = value is not null);
        command.Add((_, _) =>
        {
            var policies = GetEffectivePolicies(context);
            if (!detailed)
            {
                WriteToolPaths(context, policies);
                return ValueTask.FromResult(AltaExitCodes.Success);
            }

            foreach (var policy in policies.OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase))
            {
                WritePolicy(context, "alta.tool.item", policy);
            }

            WriteSummary(context, "alta.tool.summary", policies.Count, truncated: false);
            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static Command CreateToolCapabilityListCommand(AltaCommandContext context)
    {
        var detailed = false;
        var command = Leaf("list", "List command capability classifications.");
        command.Add("detailed", "Emit legacy per-command/runtime/provider/plugin capability records instead of one compact aggregate record.", value => detailed = value is not null);
        command.Add((_, _) =>
        {
            var policies = GetEffectivePolicies(context);
            if (!detailed)
            {
                WriteToolCapabilities(context, policies);
                return ValueTask.FromResult(AltaExitCodes.Success);
            }

            var recordCount = 0;
            foreach (var policy in policies.OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase))
            {
                WritePolicy(context, "alta.tool.capability", policy);
                recordCount++;
            }

            recordCount += WriteRuntimeCapabilities(context);
            recordCount += WriteProviderCapabilities(context);
            recordCount += WritePluginCapabilities(context, policies);
            WriteSummary(context, "alta.tool.capability.summary", recordCount, truncated: false);
            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static int WriteRuntimeCapabilities(AltaCommandContext context)
    {
        var capabilities = GetRuntimeCapabilities(context);

        foreach (var capability in capabilities)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.tool.runtimeCapability",
                version = 1,
                correlationId = context.CorrelationId,
                capability = capability.Capability,
                capability.Available,
            });
        }

        return capabilities.Length;
    }

    private static int WriteProviderCapabilities(AltaCommandContext context)
    {
        var capabilities = GetProviderCapabilities(context);

        foreach (var capability in capabilities)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                // Compatibility: keep the legacy JSONL record type stable for live-tool clients.
                type = "alta.tool.backendCapability",
                version = 1,
                correlationId = context.CorrelationId,
                providerKey = capability.ProviderKey,
                ProviderId = capability.ProviderId,
                capability.Registered,
                capability.Configured,
                capability.SupportsAltaSessionTool,
            });
        }

        return capabilities.Length;
    }

    private static (string Capability, bool Available)[] GetRuntimeCapabilities(AltaCommandContext context) =>
    [
        ("catalog.project", context.Services.Get<ProjectCatalog>() is not null),
        ("catalog.session", context.Services.Get<SessionViewCatalog>() is not null),
        ("catalog.skill", context.Services.Get<SkillCatalog>() is not null),
        ("runtime.workSession", context.Services.Get<SessionRuntimeService>() is not null),
        ("providers.agentHub", context.Services.Get<AgentHub>() is not null),
        ("plugins.altaCatalog", context.Services.Get<IAltaPluginCatalog>() is not null),
        ("sessionTool.providerPolicy", context.Services.Get<IAltaSessionToolProviderPolicy>() is not null),
    ];

    private static ProviderCapability[] GetProviderCapabilities(AltaCommandContext context)
    {
        var descriptors = GetProviderDescriptors(context);
        var policy = context.Services.Get<IAltaSessionToolProviderPolicy>();
        return GetProviderProviderIds(context, descriptors)
            .Select(ProviderId => new ProviderCapability(
                ProviderId.Value,
                ProviderId.Value,
                descriptors.Any(descriptor => string.Equals(descriptor.ProviderId.Value, ProviderId.Value, StringComparison.OrdinalIgnoreCase)),
                descriptors.Any(descriptor => string.Equals(descriptor.ProviderId.Value, ProviderId.Value, StringComparison.OrdinalIgnoreCase)),
                policy?.SupportsAltaSessionTool(ProviderId.Value) ?? false))
            .ToArray();
    }

    private static ModelProviderId[] GetProviderProviderIds(AltaCommandContext context, IReadOnlyList<ModelProviderDescriptor> descriptors)
    {
        var ProviderIds = descriptors.Select(static descriptor => new ModelProviderId(descriptor.ProviderId.Value)).ToList();
        return ProviderIds.OrderBy(static id => id.Value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int WritePluginCapabilities(AltaCommandContext context, IReadOnlyList<AltaCommandPolicy> policies)
    {
        var capability = GetPluginCapability(context, policies);
        if (capability is null)
        {
            return 0;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.tool.pluginCapability",
            version = 1,
            correlationId = context.CorrelationId,
            capability.Available,
            capability.PluginCount,
            capability.PluginCommandCount,
        });
        return 1;
    }

    private static PluginCapability? GetPluginCapability(AltaCommandContext context, IReadOnlyList<AltaCommandPolicy> policies)
    {
        if (context.Services.Get<IAltaPluginCatalog>() is not { } catalog)
        {
            return null;
        }

        var plugins = catalog.ListPlugins();
        var builtInPolicyPaths = new HashSet<string>(Policies.Select(static policy => policy.Path), StringComparer.OrdinalIgnoreCase);
        var pluginCommandCount = policies.Count(policy => !builtInPolicyPaths.Contains(policy.Path));
        return new PluginCapability(true, plugins.Count, pluginCommandCount);
    }

    private static Command CreateProviderCommand(AltaCommandContext context)
    {
        var group = Group("provider", "Inspect registered providers.");
        var model = Group("model", "Inspect provider models.");
        model.Add(CreateModelListCommand(context, providerGroupAlias: true));
        group.Add(CreateProviderListCommand(context));
        group.Add(model);
        return group;
    }

    private static Command CreateProviderListCommand(AltaCommandContext context)
    {
        var detailed = false;
        var command = Leaf("list", "List registered provider ids.");
        command.Add("detailed", "Emit one detailed metadata record per provider instead of the compact providerKeys array.", value => detailed = value is not null);
        command.Add(async (_, _) => await HandleProviderListAsync(context, detailed).ConfigureAwait(false));
        return command;
    }

    private static Command CreateModelCommand(AltaCommandContext context)
    {
        var group = Group("model", "Inspect and resolve provider model selections.");
        group.Add(CreateModelListCommand(context, providerGroupAlias: false));
        group.Add(CreateModelShowCommand(context));
        group.Add(CreateModelResolveCommand(context));
        AddHelpText(group, "Examples: `alta model list --provider codex`; `alta model resolve --same-model-as <session-id> --reasoning low`.");
        return group;
    }

    private static Command CreateModelListCommand(AltaCommandContext context, bool providerGroupAlias)
    {
        var options = new ModelListOptions();
        var command = Leaf("list", providerGroupAlias ? "List models for registered providers." : "List models for registered providers.");
        command.Add("provider=", "Provider id filter.", value => options.Provider = value);
        command.Add("contains=", "Substring filter over model id, display name, and model ref.", value => options.Contains = value);
        command.Add("reasoning=", "Only include models supporting this reasoning effort.", value => options.ReasoningEffort = ParseReasoningOption(value));
        command.Add("supports-tools", "Only include models that report tool-call support.", value => options.SupportsTools = value is not null);
        command.Add("detailed", "Emit one detailed metadata record per model instead of the compact modelRefs array.", value => options.Detailed = value is not null);
        command.Add("refs", "Compatibility alias for the default compact modelRefs array.", _ => { });
        command.Add(async (_, _) => await HandleModelListAsync(context, options).ConfigureAwait(false));
        AddHelpText(command, "Examples: `alta model list --provider anthropic --contains sonnet`; `alta model list --provider codex --reasoning low`; `alta model list --provider anthropic --detailed`.");
        return command;
    }

    private static Command CreateModelShowCommand(AltaCommandContext context)
    {
        string? modelRef = null;
        var command = Leaf("show", "Show one model by compact model ref.");
        command.Add("<model-ref>?", "Model ref: provider:model[@reasoning].", value =>
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                modelRef = value;
            }
        });
        command.Add("model-ref=", "Model ref: provider:model[@reasoning].", value => modelRef = value);
        command.Add(async (_, _) => await HandleModelShowAsync(context, modelRef).ConfigureAwait(false));
        AddHelpText(command, "Example: `alta model show --model-ref copilot:claude-sonnet-4.6@low`.");
        return command;
    }

    private static Command CreateModelResolveCommand(AltaCommandContext context)
    {
        var options = new AltaModelSelectionOptions();
        var command = Leaf("resolve", "Resolve model selection using session-create precedence rules.");
        AddModelSelectionOptions(command, options);
        command.Add(async (_, _) => await HandleModelResolveAsync(context, options).ConfigureAwait(false));
        return command;
    }

    private static Command CreatePromptCommand(AltaCommandContext context)
    {
        var group = Group("prompt", "List, inspect, and manage file-backed user or system prompts.");
        group.Add(CreatePromptListCommand(context));
        group.Add(CreatePromptShowCommand(context));
        group.Add(CreatePromptCreateCommand(context));
        group.Add(CreatePromptEditCommand(context));
        AddHelpText(
            group,
            "User prompts live under instructions/prompts/*.prompt.md and can choose a system prompt. System prompts live under instructions/system/*.system-prompt.md.",
            "Default target is user prompts; add `--system` for system prompts. `--scope` accepts global, project, builtin, or all and defaults to all.",
            "LLM workflow: run `alta prompt list` to find a prompt-id, optionally `alta prompt show <prompt-id>`, then pass it to `alta session send <session-id> --prompt-id <prompt-id> ...`.",
            "Examples:",
            "  `alta prompt list --scope all`",
            "  `alta prompt list --system --verbose --scope project`",
            "  `alta prompt show default`",
            "  `alta prompt create reviewer --scope project --name Reviewer --stdin`",
            "  `alta prompt edit my-prompt --scope global` (returns path; add `--stdin` to replace file content)");
        return group;
    }

    private static Command CreatePromptListCommand(AltaCommandContext context)
    {
        var options = new PromptManagementOptions();
        var command = Leaf("list", "Progressively list prompts as JSONL records with prompt-id/id, name, description, source, and scope.");
        AddPromptManagementOptions(command, options, includeVerbose: true, includeContentInput: false);
        command.Add(async (_, _) => await HandlePromptListAsync(context, options).ConfigureAwait(false));
        AddHelpText(command, "Each matching prompt is emitted as one `alta.prompt` record for progressive consumption. Add `--verbose` to include full prompt content.");
        return command;
    }

    private static Command CreatePromptShowCommand(AltaCommandContext context)
    {
        string? promptId = null;
        var options = new PromptManagementOptions { Verbose = true };
        var command = Leaf("show", "Show one prompt by prompt-id/id, including full content.");
        command.Add("<prompt-id>", "Prompt id from `alta prompt list`, for example `default`.", value => promptId = value);
        AddPromptManagementOptions(command, options, includeVerbose: false, includeContentInput: false);
        command.Add(async (_, _) => await HandlePromptShowAsync(context, promptId, options).ConfigureAwait(false));
        return command;
    }

    private static Command CreatePromptCreateCommand(AltaCommandContext context)
    {
        string? promptId = null;
        var options = new PromptManagementOptions { Scope = "global" };
        var command = Leaf("create", "Create a complete global/project user or system prompt file without overwriting an existing file.");
        command.Add("<prompt-id>", "Prompt id/file stem to create. Do not include directory separators or file suffixes.", value => promptId = value);
        AddPromptManagementOptions(command, options, includeVerbose: false, includeContentInput: true);
        command.Add("name=", "Display name for a user prompt; defaults to the prompt id. For --system, optional metadata name.", value => options.Name = value);
        command.Add("description=", "Optional prompt description metadata.", value => options.Description = value);
        command.Add("system-prompt-id=", "System prompt id selected by a user prompt. Defaults to `default`. Ignored with --system.", value => options.SystemPromptId = value);
        command.Add(async (_, _) => await HandlePromptCreateAsync(context, promptId, options).ConfigureAwait(false));
        AddHelpText(command, "Create requires --content or --stdin for the prompt body. User prompts write required frontmatter automatically; system prompts write optional name/description frontmatter when provided. Use `alta prompt edit` to replace an existing file.");
        return command;
    }

    private static Command CreatePromptEditCommand(AltaCommandContext context)
    {
        string? promptId = null;
        var options = new PromptManagementOptions { Scope = "global" };
        var command = Leaf("edit", "Return or update the editable global/project prompt file path.");
        command.Add("<prompt-id>", "Prompt id/file stem to edit or create.", value => promptId = value);
        AddPromptManagementOptions(command, options, includeVerbose: false, includeContentInput: true);
        command.Add(async (_, _) => await HandlePromptEditAsync(context, promptId, options).ConfigureAwait(false));
        AddHelpText(command, "Without `--content`/`--stdin`, returns the exact path an external editor should open. With content input, replaces that file. Built-in/all scopes are read-only and invalid for edit.");
        return command;
    }

    private static void AddPromptManagementOptions(Command command, PromptManagementOptions options, bool includeVerbose, bool includeContentInput)
    {
        command.Add("scope=", "Prompt source scope: global, project, builtin, or all. List/show default all; edit default global and only allows global/project.", value => options.Scope = ValidatePromptScope(value));
        command.Add("project=", "Project id, slug, or path for project-local prompts. Defaults to caller project, matching cwd catalog project, or cwd.", value => options.Project = value);
        command.Add("system", "Target system prompts under system/*.system-prompt.md. Omit for user prompts under prompts/*.prompt.md.", value => options.System = value is not null);
        if (includeVerbose)
        {
            command.Add("verbose", "Include full prompt content/body in prompt records.", value => options.Verbose = value is not null);
        }

        if (includeContentInput)
        {
            command.Add("content=", "Replacement file content. Prefer --stdin for multi-line prompt files.", value => options.Content = value);
            command.Add("stdin", "Read replacement file content from stdin.", value => options.UseStdin = value is not null);
        }
    }

    private static Command CreatePluginCommand(AltaCommandContext context)
    {
        var group = Group("plugin", "Inspect loaded/discovered plugins.");
        group.Add(CreatePluginListCommand(context));
        group.Add(CreatePluginStatusCommand(context));
        return group;
    }

    private static Command CreatePluginListCommand(AltaCommandContext context)
    {
        var detailed = false;
        var command = Leaf("list", "List plugin runtime summaries.");
        command.Add("detailed", "Emit one detailed plugin record per runtime instead of compact plugin refs.", value => detailed = value is not null);
        command.Add((_, _) => HandlePluginList(context, detailed));
        return command;
    }

    private static Command CreatePluginStatusCommand(AltaCommandContext context)
    {
        string? runtimeKey = null;
        var command = Leaf("status", "Show one plugin runtime summary.");
        command.Add("<runtime-key>", "Plugin runtime key.", value => runtimeKey = value);
        command.Add((_, _) => HandlePluginStatus(context, runtimeKey));
        return command;
    }

    private static Command Group(string name, string description)
    {
        var command = new Command(name, description)
        {
            new CommandUsage(),
            new HelpOption(),
        };
        return command;
    }

    private static Command Leaf(string name, string description)
    {
        var command = new Command(name, description)
        {
            new CommandUsage(),
            new HelpOption(),
        };
        return command;
    }

    private static void AddHelpText(Command command, params string[] lines)
    {
        command.Add("");
        foreach (var line in lines)
        {
            command.Add(line);
        }
    }

    private static void AddMessageOptions(Command command, PromptOptions options)
    {
        command.Add("message=", "Prompt/message text.", value => options.Message = value);
        command.Add("stdin", "Read prompt/message text from stdin.", value => options.UseStdin = value is not null);
    }

    private static void AddModelSelectionOptions(Command command, AltaModelSelectionOptions options)
    {
        command.Add("model-ref=", "Compact model ref provider:model[@reasoning].", value => options.ModelRef = value);
        command.Add("same-model-as=", "Inherit model selection from a session id.", value => options.SameModelAsSessionId = value);
        command.Add("provider=", "Provider id.", value => options.ProviderKey = value);
        command.Add("model=", "Model id.", value => options.ModelId = value);
        command.Add("reasoning=", "Reasoning effort: minimal, low, medium, high, xhigh, or none.", value => options.ReasoningEffort = ParseReasoningOption(value));
    }

    private static AgentReasoningEffort? ParseReasoningOption(string? value)
    {
        if (AltaModelRef.TryParseReasoning(value, out var reasoning))
        {
            return reasoning;
        }

        throw new CommandOptionException(
            $"Reasoning effort '{value}' is not valid. Use minimal, low, medium, high, xhigh, or none.",
            "--reasoning");
    }

    private static string? ValidateState(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "running" or "idle" or "inactive" or "archived" or "all"
            ? normalized
            : throw new CommandOptionException("State must be running, idle, inactive, archived, or all.", "--state");
    }

    private static string? ValidatePromptScope(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "global" or "project" or "builtin" or "all"
            ? normalized
            : throw new CommandOptionException("Prompt scope must be global, project, builtin, or all.", "--scope");
    }

    private static string? ValidateMessageKind(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "note" or "request" or "handoff" or "answer"
            ? normalized
            : throw new CommandOptionException("Message kind must be note, request, handoff, or answer.", "--kind");
    }

    private static async ValueTask<int> HandleProjectListAsync(AltaCommandContext context, bool includeArchived, bool detailed)
    {
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var projects = await catalog.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        var filtered = projects
            .Where(project => includeArchived || !project.Archived)
            .OrderBy(static project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!detailed)
        {
            WriteProjectRefs(context, filtered);
            return AltaExitCodes.Success;
        }

        foreach (var project in filtered)
        {
            WriteProject(context, "alta.project.item", project);
        }

        WriteSummary(context, "alta.project.summary", filtered.Length, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleProjectShowAsync(AltaCommandContext context, string? reference)
    {
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            return UsageError(context, "usage.missingProject", "Project reference is required.", "alta project show");
        }

        var project = await ResolveProjectAsync(catalog, reference, context, includeArchived: true).ConfigureAwait(false);
        if (project is null)
        {
            return NotFound(context, "project.notFound", $"Project '{reference}' was not found.");
        }

        WriteProject(context, "alta.project.detail", project);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleProjectResolveAsync(AltaCommandContext context, string? path)
    {
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var resolvedPath = ResolvePath(context, path ?? context.Cwd ?? Environment.CurrentDirectory);
        var project = await catalog.GetByPathAsync(resolvedPath, context.CancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return NotFound(context, "project.notFound", $"No catalog project matches path '{resolvedPath}'.");
        }

        WriteProject(context, "alta.project.resolution", project);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleProjectUpsertAsync(AltaCommandContext context, string? path)
    {
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return UsageError(context, "usage.missingPath", "Project path is required.", "alta project upsert");
        }

        var resolvedPath = ResolvePath(context, path);
        if (!Directory.Exists(resolvedPath))
        {
            return NotFound(context, "project.pathNotFound", $"Project path '{resolvedPath}' does not exist.");
        }

        var project = await catalog.UpsertFromPathAsync(resolvedPath, context.CancellationToken).ConfigureAwait(false);
        WriteProject(context, "alta.project.upserted", project);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionListAsync(
        AltaCommandContext context,
        string? projectFilter,
        string? stateFilter,
        string? providerFilter,
        int limit,
        bool includeMetrics)
    {
        if (limit <= 0)
        {
            return UsageError(context, "usage.invalidLimit", "--limit must be a positive integer.", "alta session list");
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        ProjectDescriptor? project = null;
        if (!string.IsNullOrWhiteSpace(projectFilter))
        {
            if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
            {
                return AltaExitCodes.ServiceUnavailable;
            }

            project = await ResolveProjectAsync(catalog, projectFilter, context, includeArchived: true).ConfigureAwait(false);
            if (project is null)
            {
                return NotFound(context, "project.notFound", $"Project '{projectFilter}' was not found.");
            }
        }

        var includeArchived = string.Equals(stateFilter, "archived", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(stateFilter, "all", StringComparison.OrdinalIgnoreCase);
        var filtered = infos
            .Where(info => project is null || string.Equals(info.Session.ProjectRef, project.Id, StringComparison.OrdinalIgnoreCase))
            .Where(info => string.IsNullOrWhiteSpace(providerFilter) || string.Equals(info.Session.ProviderId, providerFilter, StringComparison.OrdinalIgnoreCase))
            .Where(info => includeArchived || !string.Equals(info.State, "archived", StringComparison.OrdinalIgnoreCase))
            .Where(info => string.IsNullOrWhiteSpace(stateFilter) || string.Equals(stateFilter, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(info.State, stateFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static info => info.Session.LastActiveAt)
            .ThenBy(static info => info.Session.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit + 1)
            .ToArray();

        var truncated = filtered.Length > limit;
        var emitted = truncated ? filtered.Take(limit).ToArray() : filtered;
        var metricsBySessionId = includeMetrics
            ? await BuildCompactMetricsForListAsync(context, emitted).ConfigureAwait(false)
            : null;
        foreach (var info in emitted)
        {
            SessionMetrics? metrics = null;
            metricsBySessionId?.TryGetValue(info.Session.SessionId, out metrics);
            WriteSession(context, "alta.session.item", info, includeChildren: false, infos, metrics);
        }

        WriteSummary(context, "alta.session.summary", emitted.Length, truncated);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionShowAsync(AltaCommandContext context, string? sessionId, string recordType)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session show");
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId, includeLocalState: recordType == "alta.session.detail").ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var info = infoResult.Info!;

        var metrics = recordType == "alta.session.detail"
            ? await BuildCompactMetricsAsync(context, info).ConfigureAwait(false)
            : null;
        WriteSession(context, recordType, info, includeChildren: true, infoResult.Infos, metrics);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionMetricsAsync(AltaCommandContext context, string? sessionId, string? scope)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session metrics");
        }

        var normalizedScope = NormalizeMetricsScope(scope);
        if (normalizedScope is null)
        {
            return UsageError(context, "usage.invalidScope", "--scope must be last-turn or session.", "alta session metrics");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var info = infoResult.Info!;

        var history = await ReadSessionHistoryAsync(context, runtime, info).ConfigureAwait(false);
        var metrics = BuildSessionMetrics(info, history ?? [], normalizedScope.Value);
        WriteSessionMetrics(context, "alta.session.metrics", info, metrics);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionResultAsync(AltaCommandContext context, string? sessionId, string? scope)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session result");
        }

        var normalizedScope = NormalizeMetricsScope(scope);
        if (normalizedScope is null)
        {
            return UsageError(context, "usage.invalidScope", "--scope must be last-turn or session.", "alta session result");
        }

        var result = await BuildSessionResultAsync(context, sessionId, normalizedScope.Value).ConfigureAwait(false);
        if (result.ExitCode != AltaExitCodes.Success)
        {
            return result.ExitCode;
        }

        WriteSessionResult(context, "alta.session.result", result.Info!, result.Result!, includeResult: true, includeMetrics: true);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionReportAsync(
        AltaCommandContext context,
        IReadOnlyCollection<string> sessionIds,
        bool useStdin,
        string? scope,
        string? include)
    {
        var normalizedScope = NormalizeMetricsScope(scope);
        if (normalizedScope is null)
        {
            return UsageError(context, "usage.invalidScope", "--scope must be last-turn or session.", "alta session report");
        }

        var includes = ParseSet(include);
        if (includes.Count == 0)
        {
            includes = ParseSet("result,metrics");
        }

        if (includes.Any(static item => item is not "result" and not "metrics"))
        {
            return UsageError(context, "usage.invalidInclude", "--include values must be result and/or metrics.", "alta session report");
        }

        var ids = new List<string>(sessionIds.Where(static id => !string.IsNullOrWhiteSpace(id)).Select(static id => id.Trim()));
        if (useStdin)
        {
            var stdin = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            foreach (var id in stdin.Split(['\r', '\n', '\t', ' ', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            return UsageError(context, "usage.missingSession", "At least one session id is required.", "alta session report");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var includeResult = includes.Contains("result");
        var includeMetrics = includes.Contains("metrics");
        var reportItems = new SessionReportItem?[ids.Count];
        var buildTasks = new List<Task<SessionReportItem>>(ids.Count);
        for (var index = 0; index < ids.Count; index++)
        {
            var id = ids[index];
            var info = FindSession(infos, id);
            if (info is null)
            {
                reportItems[index] = SessionReportItem.NotFound(index, id);
                continue;
            }

            buildTasks.Add(BuildSessionReportItemAsync(context, runtime, info, normalizedScope.Value, index, id));
        }

        foreach (var item in await Task.WhenAll(buildTasks).ConfigureAwait(false))
        {
            reportItems[item.Index] = item;
        }

        var successCount = 0;
        var diagnosticCount = 0;
        foreach (var item in reportItems)
        {
            if (item is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(item.Diagnostics))
            {
                context.Stderr.Write(item.Diagnostics);
            }

            if (item.Info is null || item.Result is null)
            {
                diagnosticCount++;
                WriteSessionReportDiagnostic(context, item.SessionId, "session.notFound", AltaExitCodes.NotFound, $"Session '{item.SessionId}' was not found.");
                continue;
            }

            successCount++;
            WriteSessionResult(context, "alta.session.report.item", item.Info, item.Result, includeResult, includeMetrics);
        }

        WriteSessionReportSummary(context, ids.Count, successCount, diagnosticCount, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async Task<SessionReportItem> BuildSessionReportItemAsync(
        AltaCommandContext context,
        SessionRuntimeService runtime,
        AltaSessionInfo info,
        SessionMetricsScope scope,
        int index,
        string sessionId)
    {
        using var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        var isolatedContext = context with
        {
            Stdout = TextWriter.Null,
            Stderr = diagnostics,
        };
        var result = await BuildSessionResultAsync(isolatedContext, runtime, info, scope).ConfigureAwait(false);
        return SessionReportItem.Success(index, sessionId, info, result, diagnostics.ToString());
    }

    private static async ValueTask<int> HandleSessionChildrenAsync(AltaCommandContext context, string? sessionId, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session children");
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var parent = FindSession(infos, sessionId);
        if (parent is null)
        {
            return NotFound(context, "session.notFound", $"Session '{sessionId}' was not found.");
        }

        var children = GetChildren(infos, parent, recursive)
            .ToArray();
        foreach (var child in children)
        {
            WriteSession(context, "alta.session.item", child, includeChildren: false, infos);
        }

        WriteSummary(context, "alta.session.children.summary", children.Length, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionModelAsync(AltaCommandContext context, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session model");
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var info = infoResult.Info!;
        WriteModelSelection(context, "alta.model.selection", CreateModelSelection(info.Session, info.Preference), info.Session.SessionId);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionEventsAsync(
        AltaCommandContext context,
        string? sessionId,
        long? since,
        int limit,
        SessionEventsOptions options,
        bool fromTail)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", fromTail ? "alta session tail" : "alta session events");
        }

        if (limit <= 0)
        {
            return UsageError(context, "usage.invalidLimit", fromTail ? "--last must be positive." : "--limit must be positive.", fromTail ? "alta session tail" : "alta session events");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var info = infoResult.Info!;
        var history = await ReadSessionHistoryAsync(context, runtime, info).ConfigureAwait(false);

        if (history is null)
        {
            WriteSummary(context, fromTail ? "alta.session.tail.summary" : "alta.session.events.summary", 0, truncated: false);
            return AltaExitCodes.Success;
        }

        var includes = ParseSet(options.Include);
        var kinds = ParseSet(options.Kind);
        var fields = ParseSet(options.Fields);
        var records = history
            .Select((agentEvent, index) => (Event: agentEvent, Sequence: (long)index + 1))
            .Where(item => since is null || item.Sequence > since.Value)
            .Where(item => IncludesEvent(item.Event, includes, kinds, options.NoToolOutput))
            .ToArray();
        var selected = fromTail
            ? records.TakeLast(limit + 1).ToArray()
            : records.Take(limit + 1).ToArray();
        var truncated = selected.Length > limit;
        var emitted = truncated
            ? (fromTail ? selected.Skip(1).ToArray() : selected.Take(limit).ToArray())
            : selected;

        foreach (var item in emitted)
        {
            WriteAgentEvent(context, info.Session, item.Event, item.Sequence, fields);
        }

        WriteSummary(context, fromTail ? "alta.session.tail.summary" : "alta.session.events.summary", emitted.Length, truncated);
        return AltaExitCodes.Success;
    }

    private static async Task<IReadOnlyDictionary<string, SessionMetrics>> BuildCompactMetricsForListAsync(
        AltaCommandContext context,
        IReadOnlyList<AltaSessionInfo> infos)
    {
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return new Dictionary<string, SessionMetrics>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, SessionMetrics>(StringComparer.Ordinal);
        foreach (var info in infos)
        {
            var history = await ReadSessionHistoryAsync(context, runtime, info).ConfigureAwait(false);
            result[info.Session.SessionId] = BuildSessionMetrics(info, history ?? [], SessionMetricsScope.LastTurn);
        }

        return result;
    }

    private static async Task<SessionMetrics?> BuildCompactMetricsAsync(AltaCommandContext context, AltaSessionInfo info)
    {
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return null;
        }

        var history = await ReadSessionHistoryAsync(context, runtime, info).ConfigureAwait(false);
        return BuildSessionMetrics(info, history ?? [], SessionMetricsScope.LastTurn);
    }

    private static async Task<SessionResultBuildResult> BuildSessionResultAsync(AltaCommandContext context, string sessionId, SessionMetricsScope scope)
    {
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return SessionResultBuildResult.Fail(AltaExitCodes.ServiceUnavailable);
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return SessionResultBuildResult.Fail(infoResult.ExitCode);
        }

        var info = infoResult.Info!;

        var history = await ReadSessionHistoryAsync(context, runtime, info).ConfigureAwait(false) ?? [];
        var result = BuildSessionResult(info, history, scope);
        return SessionResultBuildResult.Success(info, result);
    }

    private static async Task<SessionResult> BuildSessionResultAsync(
        AltaCommandContext context,
        SessionRuntimeService runtime,
        AltaSessionInfo info,
        SessionMetricsScope scope)
    {
        var history = await ReadSessionHistoryAsync(context, runtime, info).ConfigureAwait(false) ?? [];
        return BuildSessionResult(info, history, scope);
    }

    private static SessionResult BuildSessionResult(AltaSessionInfo info, IReadOnlyList<AgentEvent> history, SessionMetricsScope scope)
    {
        var scoped = SelectMetricScope(history, scope);
        var metrics = BuildSessionMetrics(info, history, scope);
        var finalAssistant = scoped.OfType<AgentContentCompletedEvent>().LastOrDefault(static content => content.Kind == AgentContentKind.Assistant);
        var finalError = scoped.OfType<AgentErrorEvent>().LastOrDefault();
        var status = DetermineResultStatus(info, finalAssistant, finalError);
        var finalAnswer = status == SessionResultStatus.Completed && finalAssistant is not null
            ? finalAssistant.Content
            : null;
        var result = new SessionResult(
            scope,
            status,
            finalAnswer,
            finalAssistant?.Timestamp,
            status is SessionResultStatus.Failed or SessionResultStatus.Cancelled ? CreateFinalError(finalError!) : null,
            metrics);
        return result;
    }

    private static async Task<IReadOnlyList<AgentEvent>?> ReadSessionHistoryAsync(
        AltaCommandContext context,
        SessionRuntimeService runtime,
        AltaSessionInfo info)
    {
        var storedHistoryUnavailable = false;
        var history = await runtime.TryReadStoredHistoryAsync(
                info.Session,
                _ => storedHistoryUnavailable = true,
                context.CancellationToken)
            .ConfigureAwait(false);
        if (storedHistoryUnavailable)
        {
            AltaJsonlWriter.WriteWarning(
                context.Stderr,
                context.CorrelationId,
                "session.historyStoreUnavailable",
                "Stored session history could not be read; provider history fallback will be used when available.");
        }

        if (history is null || history.Count == 0)
        {
            try
            {
                var activeHistory = await runtime.GetHistoryAsync(info.Session.SessionId, context.CancellationToken).ConfigureAwait(false);
                if (activeHistory.Count > 0 || history is null)
                {
                    history = activeHistory;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or IOException or UnauthorizedAccessException)
            {
                AltaJsonlWriter.WriteWarning(context.Stderr, context.CorrelationId, "session.historyUnavailable", ex.Message);
            }
        }

        return history;
    }

    private static SessionMetricsScope? NormalizeMetricsScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.Equals(scope, "last-turn", StringComparison.OrdinalIgnoreCase) || string.Equals(scope, "turn", StringComparison.OrdinalIgnoreCase))
        {
            return SessionMetricsScope.LastTurn;
        }

        return string.Equals(scope, "session", StringComparison.OrdinalIgnoreCase) || string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase)
            ? SessionMetricsScope.Session
            : null;
    }

    private static SessionMetrics BuildSessionMetrics(AltaSessionInfo info, IReadOnlyList<AgentEvent> history, SessionMetricsScope scope)
    {
        var scoped = SelectMetricScope(history, scope);
        var firstEvent = scoped.FirstOrDefault();
        var lastEvent = scoped.LastOrDefault();
        var firstUser = scoped.OfType<AgentContentCompletedEvent>().FirstOrDefault(static content => content.Kind == AgentContentKind.User);
        var finalAssistant = scoped.OfType<AgentContentCompletedEvent>().LastOrDefault(static content => content.Kind == AgentContentKind.Assistant);
        var startedAt = firstUser?.Timestamp ?? firstEvent?.Timestamp;
        var completedAt = finalAssistant?.Timestamp ?? lastEvent?.Timestamp;
        var finalError = scoped.OfType<AgentErrorEvent>().LastOrDefault();
        var status = DetermineResultStatus(info, finalAssistant, finalError);
        var duration = startedAt is not null && completedAt is not null && completedAt >= startedAt
            ? completedAt.Value - startedAt.Value
            : (TimeSpan?)null;
        var toolCallCount = scoped.OfType<AgentActivityEvent>()
            .Count(static activity => IsToolLikeActivity(activity.Kind) && activity.Phase == AgentActivityPhase.Requested);
        var usageUpdates = scoped.OfType<AgentSessionUpdateEvent>()
            .Where(static update => update.Usage is not null)
            .ToArray();
        var finalUsage = usageUpdates.LastOrDefault()?.Usage;
        var providerTotals = BuildProviderOperationTotals(usageUpdates);
        var finalAnswer = finalAssistant?.Content ?? string.Empty;

        return new SessionMetrics(
            scope,
            scoped.Count,
            scoped.Select(static agentEvent => agentEvent.RunId?.Value).Where(static runId => !string.IsNullOrWhiteSpace(runId)).Distinct(StringComparer.Ordinal).Count(),
            startedAt,
            completedAt,
            duration,
            finalAssistant?.Timestamp,
            CountCompletedContent(scoped, AgentContentKind.User),
            CountCompletedContent(scoped, AgentContentKind.Assistant),
            toolCallCount,
            finalAnswer.Length,
            CountWords(finalAnswer),
            TokenEstimator.Estimate(finalAnswer),
            finalUsage,
            providerTotals,
            StatusWire(status),
            status is SessionResultStatus.Failed or SessionResultStatus.Cancelled ? CreateFinalError(finalError!) : null,
            ToModelSelectionPayload(CreateModelSelection(info.Session, info.Preference)));
    }

    private static SessionResultStatus DetermineResultStatus(AltaSessionInfo info, AgentContentCompletedEvent? finalAssistant, AgentErrorEvent? finalError)
    {
        if (info.IsRunning || string.Equals(info.State, "running", StringComparison.OrdinalIgnoreCase))
        {
            return SessionResultStatus.Running;
        }

        if (finalError is not null && (finalAssistant is null || finalError.Timestamp >= finalAssistant.Timestamp))
        {
            return IsCancellationError(finalError) ? SessionResultStatus.Cancelled : SessionResultStatus.Failed;
        }

        return finalAssistant is not null ? SessionResultStatus.Completed : SessionResultStatus.Unknown;
    }

    private static bool IsCancellationError(AgentErrorEvent error)
        => error.Exception is OperationCanceledException ||
           error.Exception is TaskCanceledException ||
           string.Equals(error.ExceptionInfo?.Type, typeof(OperationCanceledException).FullName, StringComparison.Ordinal) ||
           string.Equals(error.ExceptionInfo?.Type, typeof(TaskCanceledException).FullName, StringComparison.Ordinal) ||
           error.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase);

    private static FinalErrorPayload CreateFinalError(AgentErrorEvent error)
        => new(
            IsCancellationError(error) ? "cancelled" : "failed",
            error.Message,
            error.Timestamp,
            error.RunId?.Value,
            error.ExceptionInfo?.Type);

    private static string StatusWire(SessionResultStatus status)
        => status switch
        {
            SessionResultStatus.Completed => "completed",
            SessionResultStatus.Failed => "failed",
            SessionResultStatus.Cancelled => "cancelled",
            SessionResultStatus.Running => "running",
            _ => "unknown",
        };

    private static IReadOnlyList<AgentEvent> SelectMetricScope(IReadOnlyList<AgentEvent> history, SessionMetricsScope scope)
    {
        if (scope == SessionMetricsScope.Session || history.Count == 0)
        {
            return history;
        }

        var lastRunId = history.OfType<AgentContentCompletedEvent>()
            .Where(static content => content.Kind == AgentContentKind.Assistant && content.RunId is not null)
            .Select(static content => content.RunId!.Value.Value)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(lastRunId))
        {
            lastRunId = history.Where(static agentEvent => agentEvent.RunId is not null)
                .Select(static agentEvent => agentEvent.RunId!.Value.Value)
                .LastOrDefault();
        }

        return string.IsNullOrWhiteSpace(lastRunId)
            ? history
            : history.Where(agentEvent => string.Equals(agentEvent.RunId?.Value, lastRunId, StringComparison.Ordinal)).ToArray();
    }

    private static int CountCompletedContent(IReadOnlyList<AgentEvent> events, AgentContentKind kind)
        => events.OfType<AgentContentCompletedEvent>().Count(content => content.Kind == kind);

    private static ProviderOperationTotals BuildProviderOperationTotals(IReadOnlyList<AgentSessionUpdateEvent> usageUpdates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long inputTokens = 0;
        long outputTokens = 0;
        long cachedInputTokens = 0;
        long reasoningTokens = 0;
        var operationCount = 0;

        foreach (var usage in usageUpdates)
        {
            if (usage.Usage?.LastOperation is not { } operation)
            {
                continue;
            }

            var signature = string.Join('\u001f', usage.RunId?.Value, operation.Model, operation.InputTokens, operation.OutputTokens, operation.CachedInputTokens, operation.CacheReadTokens, operation.CacheWriteTokens, operation.ReasoningTokens, operation.ParentToolCallId, operation.Initiator);
            if (!seen.Add(signature))
            {
                continue;
            }

            operationCount++;
            inputTokens += operation.InputTokens ?? 0;
            outputTokens += operation.OutputTokens ?? 0;
            cachedInputTokens += operation.CachedInputTokens ?? 0;
            reasoningTokens += operation.ReasoningTokens ?? 0;
        }

        return new ProviderOperationTotals(operationCount, inputTokens, outputTokens, cachedInputTokens, reasoningTokens);
    }

    private static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
                continue;
            }

            if (!inWord)
            {
                count++;
                inWord = true;
            }
        }

        return count;
    }

    private static bool IsToolLikeActivity(AgentActivityKind kind)
        => kind is AgentActivityKind.ToolCall or AgentActivityKind.CommandExecution or AgentActivityKind.FileChange or AgentActivityKind.McpToolCall or AgentActivityKind.DynamicToolCall or AgentActivityKind.CollabAgentToolCall;

    private static async ValueTask<int> HandleSessionCreateAsync(AltaCommandContext context, SessionCreateOptions options)
    {
        if (options.Global && !string.IsNullOrWhiteSpace(options.Project))
        {
            return UsageError(context, "usage.projectAndGlobal", "Use either --project or --global, not both.", "alta session create");
        }

        if (!options.Global && string.IsNullOrWhiteSpace(options.Project))
        {
            return UsageError(context, "usage.missingScope", "Session create requires --project <ref> or --global.", "alta session create");
        }

        if (!string.IsNullOrWhiteSpace(options.ParentSessionId) && options.NoParent)
        {
            return UsageError(context, "usage.parentConflict", "Use either --parent or --no-parent, not both.", "alta session create");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime) ||
            !context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        ProjectDescriptor? project = null;
        if (!options.Global)
        {
            project = await ResolveProjectAsync(catalog, options.Project, context, includeArchived: false).ConfigureAwait(false);
            if (project is null)
            {
                return NotFound(context, "project.notFound", $"Project '{options.Project}' was not found.");
            }

        }

        var parentResolution = await ResolveParentSessionIdAsync(context, project, options).ConfigureAwait(false);
        if (parentResolution.ExitCode != AltaExitCodes.Success)
        {
            return parentResolution.ExitCode;
        }

        var modelSelection = await ResolveModelSelectionAsync(context, options.Model, "alta session create").ConfigureAwait(false);
        if (modelSelection.ExitCode != AltaExitCodes.Success)
        {
            return modelSelection.ExitCode;
        }

        var workingDirectory = project?.ProjectPath ?? GetGlobalRootOrCwd(context);
        string? createdSessionId = null;
        var createdBy = CreateProvenance(context);
        var executionOptions = BuildExecutionOptions(
            context,
            modelSelection.Selection!,
            workingDirectory,
            project is null ? [] : [project.ProjectPath],
            () => createdSessionId,
            project?.Id);

        SessionViewDescriptor session;
        if (project is null)
        {
            session = await runtime.CreateGlobalSessionAsync(executionOptions, options.Title, parentResolution.ParentSessionId, createdBy, context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            session = await runtime.CreateProjectSessionAsync(project, executionOptions, options.Title, parentResolution.ParentSessionId, createdBy, context.CancellationToken).ConfigureAwait(false);
        }

        createdSessionId = session.SessionId;
        session.ParentSessionId = parentResolution.ParentSessionId;
        session.CreatedBy = createdBy;
        await runtime.PersistSessionLocalStateAsync(session, context.CancellationToken).ConfigureAwait(false);
        await PersistSessionPreferenceAsync(context, session, modelSelection.Selection!).ConfigureAwait(false);

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.created",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = session.SessionId,
            ProviderId = session.ProviderId,
            providerKey = session.ResolvedProviderKey,
            projectId = session.ProjectRef,
            title = session.Title,
            parentSessionId = session.ParentSessionId,
            createdBy = session.CreatedBy,
            modelSelection = ToModelSelectionPayload(modelSelection.Selection!),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionSendAsync(
        AltaCommandContext context,
        string? sessionId,
        PromptOptions options,
        PromptDispatchKind kind)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session send");
        }

        var promptResult = await ReadPromptAsync(context, options, kind).ConfigureAwait(false);
        if (promptResult.ExitCode != AltaExitCodes.Success)
        {
            return promptResult.ExitCode;
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infoResult = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        var info = infoResult.Info!;
        var inputText = kind is PromptDispatchKind.Message or PromptDispatchKind.Request
            ? BuildPeerAgentMessage(context, info.Session, options, promptResult.Prompt!)
            : promptResult.Prompt!;
        if (!string.IsNullOrWhiteSpace(options.PromptId))
        {
            var validationExitCode = await ValidateSessionUserPromptIdAsync(context, info, options.PromptId).ConfigureAwait(false);
            if (validationExitCode != AltaExitCodes.Success)
            {
                return validationExitCode;
            }
        }

        var executionOptions = await BuildExecutionOptionsForSessionAsync(context, info, options.PromptId).ConfigureAwait(false);
        var agentInput = AgentInput.Text(inputText);

        try
        {
            if (kind == PromptDispatchKind.Queue)
            {
                var queueItem = await runtime.QueuePromptAsync(
                    info.Session,
                    inputText,
                    kind.ToString().ToLowerInvariant(),
                    CreateProvenance(context),
                    context.CancellationToken).ConfigureAwait(false);
                WritePromptResult(context, "alta.session.queued", info.Session, null, queueItem.QueueItemId, queued: true, kind, inputText);
                return AltaExitCodes.Success;
            }

            if (options.QueueIfBusy && await runtime.HasActiveRunAsync(info.Session, context.CancellationToken).ConfigureAwait(false))
            {
                var queueItem = await runtime.QueuePromptAsync(
                    info.Session,
                    inputText,
                    kind.ToString().ToLowerInvariant(),
                    CreateProvenance(context),
                    context.CancellationToken).ConfigureAwait(false);
                WritePromptResult(context, "alta.session.queued", info.Session, null, queueItem.QueueItemId, queued: true, kind, inputText);
                return AltaExitCodes.Success;
            }

            var augmentation = await BuildPluginAgentRunAugmentationAsync(context, info, executionOptions, agentInput).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(augmentation.CancelReason))
            {
                return PermissionDenied(context, "plugin.runCancelled", augmentation.CancelReason);
            }

            agentInput = augmentation.Input;
            executionOptions = augmentation.ExecutionOptions;

            if (kind == PromptDispatchKind.Steer)
            {
                var runId = await runtime.SteerAsync(
                    info.Session,
                    executionOptions,
                    new AgentSteerOptions { Input = agentInput },
                    context.CancellationToken).ConfigureAwait(false);
                await PersistPromptProvenanceAsync(context, info.Session, runId.Value, queued: false, kind, inputText).ConfigureAwait(false);
                WritePromptResult(context, "alta.session.steered", info.Session, runId.Value, null, queued: false, kind, inputText);
                return AltaExitCodes.Success;
            }

            var sendTask = runtime.SendAsync(
                info.Session,
                executionOptions,
                new AgentSendOptions { Input = agentInput },
                ShouldDetachPromptSubmission(context) ? CancellationToken.None : context.CancellationToken);

            if (ShouldDetachPromptSubmission(context) && await WaitForAgentSubmissionAckAsync(runtime, info.Session, sendTask).ConfigureAwait(false) && !sendTask.IsCompleted)
            {
                _ = ObserveDetachedPromptSubmissionAsync(sendTask);
                await runtime.PersistSessionLocalStateAsync(info.Session, CancellationToken.None).ConfigureAwait(false);
                await PersistPromptProvenanceAsync(context, info.Session, runId: null, queued: false, kind, inputText).ConfigureAwait(false);
                WritePromptResult(context, kind is PromptDispatchKind.Message or PromptDispatchKind.Request ? "alta.session.message.sent" : "alta.session.submitted", info.Session, runId: null, queueItemId: null, queued: false, kind, inputText);
                return AltaExitCodes.Success;
            }

            var submittedRunId = await sendTask.ConfigureAwait(false);
            await runtime.PersistSessionLocalStateAsync(info.Session, context.CancellationToken).ConfigureAwait(false);
            await PersistPromptProvenanceAsync(context, info.Session, submittedRunId.Value, queued: false, kind, inputText).ConfigureAwait(false);
            WritePromptResult(context, kind is PromptDispatchKind.Message or PromptDispatchKind.Request ? "alta.session.message.sent" : "alta.session.submitted", info.Session, submittedRunId.Value, null, queued: false, kind, inputText);
            return AltaExitCodes.Success;
        }
        catch (Exception ex) when (kind == PromptDispatchKind.Steer && ex is InvalidOperationException or NotSupportedException)
        {
            return Unsupported(context, "session.steerUnsupported", ex.Message);
        }
    }

    private static async ValueTask<int> HandleSessionAbortAsync(AltaCommandContext context, string? sessionId, string? reason)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session abort");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (info.ExitCode != AltaExitCodes.Success)
        {
            return info.ExitCode;
        }

        try
        {
            await runtime.AbortAsync(sessionId, context.CancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return Unsupported(context, "session.abortUnsupported", ex.Message);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.aborted",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId,
            reason,
            abortedBy = CreateProvenance(context),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionCompactAsync(AltaCommandContext context, string? sessionId, bool submit)
    {
        if (submit)
        {
            return Unsupported(context, "session.compactSubmitUnsupported", "--submit after compaction is not supported by the current runtime.");
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", "alta session compact");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (info.ExitCode != AltaExitCodes.Success)
        {
            return info.ExitCode;
        }

        var sessionInfo = info.Info!;
        var executionOptions = await BuildExecutionOptionsForSessionAsync(context, sessionInfo, promptId: null).ConfigureAwait(false);
        await runtime.CompactAsync(sessionInfo.Session, executionOptions, context.CancellationToken).ConfigureAwait(false);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.compacted",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = sessionInfo.Session.SessionId,
            compactedBy = CreateProvenance(context),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleReminderCreateAsync(AltaCommandContext context, ReminderCreateOptions options)
    {
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out _))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (!TryGetReminderService(context, out var reminderService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (!TryParseReminderDuration(options.Duration, out var duration, out var durationError))
        {
            return UsageError(context, "usage.invalidDuration", durationError, "alta reminder create");
        }

        if (!TryParseReminderRepeat(options.Repeat, out var repeat, out var repeatError))
        {
            return UsageError(context, "usage.invalidRepeat", repeatError, "alta reminder create");
        }

        var contentResult = await ReadReminderContentAsync(context, options).ConfigureAwait(false);
        if (contentResult.ExitCode != AltaExitCodes.Success)
        {
            return contentResult.ExitCode;
        }

        var targetSessionId = FirstNonEmpty(options.SessionId, context.Caller.SourceSessionId);
        if (string.IsNullOrWhiteSpace(targetSessionId))
        {
            return UsageError(context, "usage.missingSession", "A target session id is required outside a session caller. Use --session <session-id>.", "alta reminder create");
        }

        var infoResult = await ResolveSessionInfoAsync(context, targetSessionId).ConfigureAwait(false);
        if (infoResult.ExitCode != AltaExitCodes.Success)
        {
            return infoResult.ExitCode;
        }

        AltaReminderDescriptor descriptor;
        try
        {
            descriptor = reminderService.Create(new AltaReminderCreateRequest
            {
                TargetSessionId = infoResult.Info!.Session.SessionId,
                Content = contentResult.Prompt!,
                Duration = duration,
                RepeatCount = repeat,
                SourceSessionId = context.Caller.SourceSessionId,
                SourceAgentId = context.Caller.SourceAgentId,
                SourceProjectId = context.Caller.SourceProjectId,
                PluginRuntimeKey = context.Caller.PluginRuntimeKey,
                Cwd = context.Cwd,
            });
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return UsageError(context, "usage.invalidReminder", ex.Message, "alta reminder create");
        }
        catch (ArgumentException ex)
        {
            return UsageError(context, "usage.invalidReminder", ex.Message, "alta reminder create");
        }

        WriteReminder(context, "alta.reminder.created", descriptor);
        return AltaExitCodes.Success;
    }

    private static int HandleReminderList(AltaCommandContext context, string? sessionId, bool includeCompleted)
    {
        if (!TryGetReminderService(context, out var reminderService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var reminders = reminderService.List(sessionId, includeCompleted);
        foreach (var reminder in reminders)
        {
            WriteReminder(context, "alta.reminder.item", reminder);
        }

        WriteSummary(context, "alta.reminder.summary", reminders.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int HandleReminderDelete(AltaCommandContext context, string? reminderId)
    {
        if (string.IsNullOrWhiteSpace(reminderId))
        {
            return UsageError(context, "usage.missingReminder", "Reminder id is required.", "alta reminder delete");
        }

        if (!TryGetReminderService(context, out var reminderService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (!reminderService.TryDelete(reminderId, out var descriptor))
        {
            return NotFound(context, "reminder.notFound", $"Reminder '{reminderId}' was not found.");
        }

        WriteReminder(context, "alta.reminder.deleted", descriptor!);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandlePromptListAsync(AltaCommandContext context, PromptManagementOptions options)
    {
        var queryResult = await BuildPromptQueryAsync(context, options.Project).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var catalog = new UserPromptCatalog();
        var records = options.System
            ? catalog.ListSystemPrompts(queryResult.Query!).Where(prompt => ScopeMatches(options.Scope, prompt.SourceKind)).Select(prompt => CreateSystemPromptRecord(context, prompt, options.Verbose))
            : catalog.ListPrompts(queryResult.Query!).Where(prompt => ScopeMatches(options.Scope, prompt.SourceKind)).Select(prompt => CreateUserPromptRecord(context, prompt, options.Verbose));
        var count = 0;
        foreach (var record in records)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, record);
            count++;
        }

        WriteSummary(context, "alta.prompt.summary", count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandlePromptShowAsync(AltaCommandContext context, string? promptId, PromptManagementOptions options)
    {
        if (string.IsNullOrWhiteSpace(promptId))
        {
            return UsageError(context, "usage.missingPrompt", "Prompt id is required.", "alta prompt show");
        }

        var queryResult = await BuildPromptQueryAsync(context, options.Project).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var catalog = new UserPromptCatalog();
        if (options.System)
        {
            var prompt = SelectPromptForShow(catalog.ListSystemPrompts(queryResult.Query!), promptId.Trim(), options.Scope);
            if (prompt is null)
            {
                return NotFound(context, "prompt.notFound", $"System prompt '{promptId}' was not found in scope '{options.Scope}'.");
            }

            AltaJsonlWriter.WriteRecord(context.Stdout, CreateSystemPromptRecord(context, prompt, includeContent: true));
            return AltaExitCodes.Success;
        }

        var userPrompt = SelectPromptForShow(catalog.ListPrompts(queryResult.Query!), promptId.Trim(), options.Scope);
        if (userPrompt is null)
        {
            return NotFound(context, "prompt.notFound", $"User prompt '{promptId}' was not found in scope '{options.Scope}'.");
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, CreateUserPromptRecord(context, userPrompt, includeContent: true));
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandlePromptCreateAsync(AltaCommandContext context, string? promptId, PromptManagementOptions options)
    {
        const string CommandPath = "alta prompt create";
        if (string.IsNullOrWhiteSpace(promptId))
        {
            return UsageError(context, "usage.missingPrompt", "Prompt id is required.", CommandPath);
        }

        if (options.Scope is "builtin" or "all")
        {
            return UsageError(context, "usage.invalidScope", "Prompt create requires --scope global or --scope project; built-in/all prompts are not editable.", CommandPath);
        }

        if (!IsSafePromptFileStem(promptId))
        {
            return UsageError(context, "usage.invalidPrompt", "Prompt id must be a file name without directory separators or suffixes.", CommandPath);
        }

        if (!string.IsNullOrWhiteSpace(options.Content) && options.UseStdin)
        {
            return UsageError(context, "usage.contentConflict", "Use either --content or --stdin, not both.", CommandPath);
        }

        string? body = null;
        if (!string.IsNullOrWhiteSpace(options.Content))
        {
            body = options.Content;
        }
        else if (options.UseStdin)
        {
            body = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
        }

        body = NormalizeOptionalText(body);
        if (body is null)
        {
            return UsageError(context, "usage.missingContent", "Prompt create requires a non-empty prompt body via --content or --stdin.", CommandPath);
        }

        var queryResult = await BuildPromptQueryAsync(context, options.Project).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var catalog = new UserPromptCatalog();
        var directory = options.Scope == "project"
            ? options.System ? catalog.ResolveProjectSystemPromptDirectory(queryResult.Query!) : AppendPromptSubdirectory(catalog.ResolveProjectPromptDirectory(queryResult.Query!), "prompts")
            : options.System ? catalog.ResolveUserSystemPromptDirectory(queryResult.Query!) : Path.Combine(catalog.ResolveUserPromptDirectory(queryResult.Query!), "prompts");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return UsageError(context, "usage.missingProject", "Project prompt creation requires a project root. Provide --project or invoke from a project directory.", CommandPath);
        }

        var normalizedPromptId = promptId.Trim();
        var suffix = options.System ? ".system-prompt.md" : ".prompt.md";
        var path = Path.Combine(directory, normalizedPromptId + suffix);
        if (File.Exists(path))
        {
            return UsageError(context, "usage.promptExists", $"Prompt file already exists at '{path}'. Use `alta prompt edit` to replace it.", CommandPath);
        }

        var name = NormalizeOptionalText(options.Name) ?? normalizedPromptId;
        var description = NormalizeOptionalText(options.Description);
        var systemPromptId = NormalizeOptionalText(options.SystemPromptId) ?? UserPromptCatalog.DefaultPromptName;
        var content = options.System
            ? BuildSystemPromptCreateFile(body, NormalizeOptionalText(options.Name), description)
            : BuildUserPromptCreateFile(name, description, systemPromptId, body);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, content, context.CancellationToken).ConfigureAwait(false);

        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.prompt.created",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["promptId"] = normalizedPromptId,
            ["id"] = normalizedPromptId,
            ["name"] = name,
            ["description"] = description,
            ["promptKind"] = options.System ? "system" : "user",
            ["scope"] = options.Scope,
            ["source"] = options.Scope,
            ["path"] = path,
            ["created"] = true,
        };
        if (!options.System)
        {
            record["systemPromptId"] = systemPromptId;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandlePromptEditAsync(AltaCommandContext context, string? promptId, PromptManagementOptions options)
    {
        if (string.IsNullOrWhiteSpace(promptId))
        {
            return UsageError(context, "usage.missingPrompt", "Prompt id is required.", "alta prompt edit");
        }

        if (options.Scope is "builtin" or "all")
        {
            return UsageError(context, "usage.invalidScope", "Prompt edit requires --scope global or --scope project; built-in/all prompts are not editable.", "alta prompt edit");
        }

        if (!IsSafePromptFileStem(promptId))
        {
            return UsageError(context, "usage.invalidPrompt", "Prompt id must be a file name without directory separators.", "alta prompt edit");
        }

        if (!string.IsNullOrWhiteSpace(options.Content) && options.UseStdin)
        {
            return UsageError(context, "usage.contentConflict", "Use either --content or --stdin, not both.", "alta prompt edit");
        }

        var queryResult = await BuildPromptQueryAsync(context, options.Project).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var catalog = new UserPromptCatalog();
        var directory = options.Scope == "project"
            ? options.System ? catalog.ResolveProjectSystemPromptDirectory(queryResult.Query!) : AppendPromptSubdirectory(catalog.ResolveProjectPromptDirectory(queryResult.Query!), "prompts")
            : options.System ? catalog.ResolveUserSystemPromptDirectory(queryResult.Query!) : Path.Combine(catalog.ResolveUserPromptDirectory(queryResult.Query!), "prompts");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return UsageError(context, "usage.missingProject", "Project prompt editing requires a project root. Provide --project or invoke from a project directory.", "alta prompt edit");
        }

        var suffix = options.System ? ".system-prompt.md" : ".prompt.md";
        var path = Path.Combine(directory, promptId.Trim() + suffix);
        string? content = null;
        if (!string.IsNullOrWhiteSpace(options.Content))
        {
            content = options.Content;
        }
        else if (options.UseStdin)
        {
            content = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
        }

        if (content is not null)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, content, context.CancellationToken).ConfigureAwait(false);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.prompt.edit",
            version = 1,
            correlationId = context.CorrelationId,
            promptId = promptId.Trim(),
            id = promptId.Trim(),
            promptKind = options.System ? "system" : "user",
            scope = options.Scope,
            source = options.Scope,
            path,
            exists = File.Exists(path),
            updated = content is not null,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSkillListAsync(AltaCommandContext context, string? projectRef, bool detailed)
    {
        if (!context.TryGetRequired<SkillCatalog>(nameof(SkillCatalog), out var skillCatalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var queryResult = await BuildSkillQueryAsync(context, projectRef).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var skills = await skillCatalog.ListAsync(queryResult.Query!, context.CancellationToken).ConfigureAwait(false);
        if (!detailed)
        {
            WriteSkillRefs(context, skills);
            return AltaExitCodes.Success;
        }

        foreach (var skill in skills)
        {
            WriteSkill(context, "alta.skill.item", skill, includeDiagnostics: true);
        }

        WriteSummary(context, "alta.skill.summary", skills.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSkillShowAsync(AltaCommandContext context, string? skillName, string? projectRef)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return UsageError(context, "usage.missingSkill", "Skill name is required.", "alta skill show");
        }

        if (!context.TryGetRequired<SkillCatalog>(nameof(SkillCatalog), out var skillCatalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var queryResult = await BuildSkillQueryAsync(context, projectRef).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var document = await skillCatalog.GetAsync(queryResult.Query!, skillName, context.CancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return NotFound(context, "skill.notFound", $"Skill '{skillName}' was not found.");
        }

        WriteSkill(context, "alta.skill.detail", document.Descriptor, includeDiagnostics: true, document);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSkillActivateAsync(AltaCommandContext context, string? skillName, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return UsageError(context, "usage.missingSkill", "Skill name is required.", "alta skill activate");
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "--session <session-id> is required.", "alta skill activate");
        }

        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = await ResolveSessionInfoAsync(context, sessionId).ConfigureAwait(false);
        if (info.ExitCode != AltaExitCodes.Success)
        {
            return info.ExitCode;
        }

        var sessionInfo = info.Info!;
        var executionOptions = await BuildExecutionOptionsForSessionAsync(context, sessionInfo, promptId: null).ConfigureAwait(false);
        try
        {
            var runId = await runtime.ActivateSkillAsync(sessionInfo.Session, executionOptions, skillName, context.CancellationToken).ConfigureAwait(false);
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.skill.activated",
                version = 1,
                correlationId = context.CorrelationId,
                skillName,
                sessionId = sessionInfo.Session.SessionId,
                runId = runId.Value,
                activatedBy = CreateProvenance(context),
            });
            return AltaExitCodes.Success;
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(context, "skill.notFound", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Unsupported(context, "skill.activationUnsupported", ex.Message);
        }
    }

    private static async ValueTask<int> HandleProviderListAsync(AltaCommandContext context, bool detailed)
    {
        var descriptors = GetProviderDescriptors(context);
        var ProviderIds = GetProviderProviderIds(context, descriptors);

        if (!detailed)
        {
            WriteProviderKeys(context, ProviderIds);
            await Task.CompletedTask.ConfigureAwait(false);
            return AltaExitCodes.Success;
        }

        foreach (var ProviderId in ProviderIds)
        {
            var descriptor = descriptors.FirstOrDefault(candidate => string.Equals(candidate.ProviderId.Value, ProviderId.Value, StringComparison.OrdinalIgnoreCase));
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.provider.item",
                version = 1,
                correlationId = context.CorrelationId,
                providerKey = ProviderId.Value,
                ProviderId = ProviderId.Value,
                displayName = descriptor?.DisplayName,
            });
        }

        WriteSummary(context, "alta.provider.summary", ProviderIds.Length, truncated: false);
        await Task.CompletedTask.ConfigureAwait(false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleModelListAsync(AltaCommandContext context, ModelListOptions options)
    {
        if (!context.TryGetRequired<IModelProviderInitializationService>(nameof(IModelProviderInitializationService), out var providerInitializationService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var providerIds = context.Services.Get<IModelProviderRegistry>()?.ListProviders()
            .Select(static descriptor => descriptor.ProviderId)
            ?? providerInitializationService.CurrentStates.Select(static state => state.ProviderId);
        var providers = providerIds
            .Where(id => string.IsNullOrWhiteSpace(options.Provider) || string.Equals(id.Value, options.Provider, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static id => id.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(options.Provider) && providers.Length == 0)
        {
            return NotFound(context, "provider.notFound", $"Provider '{options.Provider}' was not found.");
        }

        var count = 0;
        var modelRefs = options.Detailed ? null : new List<string>();
        foreach (var provider in providers)
        {
            IReadOnlyList<AgentModelInfo> models;
            try
            {
                models = await providerInitializationService.GetModelsAsync(provider, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or NotSupportedException)
            {
                AltaJsonlWriter.WriteWarning(context.Stderr, context.CorrelationId, "model.listUnavailable", $"Provider '{provider.Value}' models are unavailable: {ex.Message}");
                continue;
            }

            foreach (var model in models
                         .Where(model => ModelMatchesFilters(provider.Value, model, options))
                         .OrderBy(static model => model.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (options.Detailed)
                {
                    WriteModelItem(context, provider.Value, model, options.ReasoningEffort);
                }
                else
                {
                    modelRefs!.Add(CreateModelRef(provider.Value, model, options.ReasoningEffort));
                }

                count++;
            }
        }

        if (options.Detailed)
        {
            WriteSummary(context, "alta.model.summary", count, truncated: false);
        }
        else
        {
            WriteModelRefs(context, modelRefs ?? []);
        }

        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleModelShowAsync(AltaCommandContext context, string? modelRef)
    {
        if (string.IsNullOrWhiteSpace(modelRef))
        {
            return UsageError(context, "usage.missingModelRef", "Model ref is required.", "alta model show");
        }

        if (!AltaModelRef.TryParse(modelRef, out var selection, out var error))
        {
            return UsageError(context, "usage.invalidModelRef", error!, "alta model show");
        }

        if (!context.TryGetRequired<IModelProviderInitializationService>(nameof(IModelProviderInitializationService), out var providerInitializationService))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var provider = new ModelProviderId(selection.ProviderKey);
        IReadOnlyList<AgentModelInfo> models;
        try
        {
            models = await providerInitializationService.GetModelsAsync(provider, context.CancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(context, "provider.notFound", $"Provider '{selection.ProviderKey}' was not found.");
        }

        var model = models.FirstOrDefault(candidate => string.Equals(candidate.Id, selection.ModelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return NotFound(context, "model.notFound", $"Model '{selection.ModelId}' was not found for provider '{selection.ProviderKey}'.");
        }

        WriteModelItem(context, selection.ProviderKey, model, selection.ReasoningEffort);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleModelResolveAsync(AltaCommandContext context, AltaModelSelectionOptions options)
    {
        var result = await ResolveModelSelectionAsync(context, options, "alta model resolve").ConfigureAwait(false);
        if (result.ExitCode != AltaExitCodes.Success)
        {
            return result.ExitCode;
        }

        WriteModelSelection(context, "alta.model.selection", result.Selection!, sessionId: null);
        return AltaExitCodes.Success;
    }

    private static ValueTask<int> HandlePluginList(AltaCommandContext context, bool detailed)
    {
        var catalog = context.Services.Get<IAltaPluginCatalog>();
        var plugins = catalog?.ListPlugins() ?? [];
        if (!detailed)
        {
            WritePluginRefs(context, plugins);
            return ValueTask.FromResult(AltaExitCodes.Success);
        }

        foreach (var plugin in plugins)
        {
            WritePlugin(context, "alta.plugin.item", plugin);
        }

        WriteSummary(context, "alta.plugin.summary", plugins.Count, truncated: false);
        return ValueTask.FromResult(AltaExitCodes.Success);
    }

    private static ValueTask<int> HandlePluginStatus(AltaCommandContext context, string? runtimeKey)
    {
        if (string.IsNullOrWhiteSpace(runtimeKey))
        {
            return ValueTask.FromResult(UsageError(context, "usage.missingPlugin", "Plugin runtime key is required.", "alta plugin status"));
        }

        var catalog = context.Services.Get<IAltaPluginCatalog>();
        var plugin = catalog?.GetPlugin(runtimeKey);
        if (plugin is null)
        {
            return ValueTask.FromResult(NotFound(context, "plugin.notFound", $"Plugin '{runtimeKey}' was not found."));
        }

        WritePlugin(context, "alta.plugin.status", plugin);
        return ValueTask.FromResult(AltaExitCodes.Success);
    }

    private static async Task<IReadOnlyList<AltaSessionInfo>?> LoadSessionInfosAsync(AltaCommandContext context)
    {
        var queryService = context.Services.Get<IAltaSessionQueryService>();
        if (queryService is null &&
            context.Services.Get<SessionRuntimeService>() is null &&
            context.Services.Get<SessionViewCatalog>() is null)
        {
            await foreach (var _ in new AltaSessionQueryService().LoadAsync(context).ConfigureAwait(false))
            {
            }

            return null;
        }

        queryService ??= new AltaSessionQueryService();
        var infos = new List<AltaSessionInfo>();
        await foreach (var info in queryService.LoadAsync(context).ConfigureAwait(false))
        {
            infos.Add(info);
        }

        return infos;
    }

    private static bool TryGetReminderService(AltaCommandContext context, out AltaReminderService reminderService)
    {
        reminderService = context.Services.Get<AltaReminderService>()!;
        if (reminderService is not null)
        {
            return true;
        }

        if (context.Services is AltaServiceCollection services)
        {
            reminderService = new AltaReminderService(services);
            services.Add(reminderService);
            return true;
        }

        AltaJsonlWriter.WriteError(
            context.Stderr,
            context.CorrelationId,
            "service.unavailable",
            AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'AltaReminderService' is unavailable.");
        return false;
    }

    private static async Task<SessionInfoResolutionResult> ResolveSessionInfoAsync(AltaCommandContext context, string sessionId, bool includeLocalState = false)
    {
        if (context.Services.Get<SessionRuntimeService>() is { } runtime)
        {
            var activeSession = await runtime.TryGetActiveSessionDescriptorAsync(sessionId, context.CancellationToken).ConfigureAwait(false);
            if (activeSession is not null)
            {
                var isRunning = await runtime.HasActiveRunAsync(activeSession, context.CancellationToken).ConfigureAwait(false);
                var localState = includeLocalState && context.Services.Get<SessionViewCatalog>() is { } sessionCatalog
                    ? await TryReadLatestSessionStateAsync(sessionCatalog, activeSession, context.CancellationToken).ConfigureAwait(false)
                    : null;
                var activeInfo = new AltaSessionInfo(
                    activeSession,
                    localState,
                    null,
                    isRunning,
                    true,
                    isRunning ? "running" : "idle");
                return SessionInfoResolutionResult.Success(activeInfo, [activeInfo]);
            }
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return SessionInfoResolutionResult.Fail(AltaExitCodes.ServiceUnavailable);
        }

        var info = FindSession(infos, sessionId);
        return info is null
            ? SessionInfoResolutionResult.Fail(NotFound(context, "session.notFound", $"Session '{sessionId}' was not found."))
            : SessionInfoResolutionResult.Success(info, infos);
    }

    private static AltaSessionInfo? FindSession(IReadOnlyList<AltaSessionInfo> infos, string sessionId)
        => infos.FirstOrDefault(info =>
            string.Equals(info.Session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<AltaSessionInfo> GetChildren(IReadOnlyList<AltaSessionInfo> infos, AltaSessionInfo parent, bool recursive)
    {
        var directChildren = infos
            .Where(info => string.Equals(info.Session.ParentSessionId, parent.Session.SessionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static info => info.Session.LastActiveAt)
            .ToArray();
        foreach (var child in directChildren)
        {
            yield return child;
            if (!recursive)
            {
                continue;
            }

            foreach (var descendant in GetChildren(infos, child, recursive: true))
            {
                yield return descendant;
            }
        }
    }

    private static SessionExecutionOptions BuildExecutionOptions(
        AltaCommandContext context,
        AltaModelSelection selection,
        string workingDirectory,
        IReadOnlyList<string> projectRoots,
        Func<string?>? sourceSessionIdProvider,
        string? sourceProjectId)
        => new()
        {
            ProviderId = new ModelProviderId(selection.ProviderKey),
            ProviderKey = selection.ProviderKey,
            WorkingDirectory = workingDirectory,
            ProjectRoots = projectRoots,
            Model = selection.ModelId,
            ReasoningEffort = selection.ReasoningEffort,
            Tools = CreateAltaSessionTools(context, selection.ProviderKey, sourceSessionIdProvider, sourceProjectId, workingDirectory),
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnUserInputRequest = static (_, _) => Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>(StringComparer.Ordinal))),
        };

    private static async Task<SessionExecutionOptions> BuildExecutionOptionsForSessionAsync(AltaCommandContext context, AltaSessionInfo info, string? promptId)
    {
        var projectRoots = new List<string>();
        var workingDirectory = info.Session.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(info.Session.ProjectRef) && context.Services.Get<ProjectCatalog>() is { } catalog)
        {
            var project = await catalog.GetByIdAsync(info.Session.ProjectRef, context.CancellationToken).ConfigureAwait(false);
            if (project is not null)
            {
                workingDirectory = project.ProjectPath;
                projectRoots.Add(project.ProjectPath);
            }
        }

        return new SessionExecutionOptions
        {
            ProviderId = new ModelProviderId(info.Session.ResolvedProviderKey),
            ProviderKey = info.Session.ResolvedProviderKey,
            WorkingDirectory = workingDirectory,
            ProjectRoots = projectRoots,
            Model = info.Preference?.ModelId ?? info.Session.ModelId,
            ReasoningEffort = info.Preference?.ReasoningEffort ?? info.Session.ReasoningEffort,
            UserPromptName = NormalizeOptionalText(promptId),
            Tools = CreateAltaSessionTools(context, info.Session.ProviderId, () => info.Session.SessionId, info.Session.ProjectRef, workingDirectory),
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnUserInputRequest = static (_, _) => Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>(StringComparer.Ordinal))),
        };
    }

    private static async Task<AltaPluginAgentRunAugmentation> BuildPluginAgentRunAugmentationAsync(
        AltaCommandContext context,
        AltaSessionInfo info,
        SessionExecutionOptions executionOptions,
        AgentInput input)
    {
        var pluginBridge = context.Services.Get<PluginOrchestrationBridge>();
        if (pluginBridge is null)
        {
            return new AltaPluginAgentRunAugmentation(executionOptions, input);
        }

        var pluginOptions = new PluginAdapterOperationOptions
        {
            ProjectId = info.Session.ProjectRef,
            ProjectPath = executionOptions.WorkingDirectory,
            SessionId = info.Session.SessionId,
            ProviderId = executionOptions.ProviderId.Value,
            Model = executionOptions.Model,
            IsCodeAltaManagedProvider = IsCodeAltaManagedProvider(executionOptions.ProviderId),
        };
        var augmentation = await pluginBridge.BuildAgentRunAugmentationAsync(executionOptions, input, pluginOptions, context.CancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(augmentation.CancelReason))
        {
            return new AltaPluginAgentRunAugmentation(executionOptions, input, augmentation.CancelReason);
        }

        return new AltaPluginAgentRunAugmentation(
            CopyExecutionOptions(executionOptions, augmentation),
            augmentation.Input ?? input);
    }

    private static SessionExecutionOptions CopyExecutionOptions(SessionExecutionOptions source, PluginAgentRunAugmentation augmentation)
        => new()
        {
            ProviderId = source.ProviderId,
            ProviderKey = source.ProviderKey,
            WorkingDirectory = source.WorkingDirectory,
            ProjectRoots = source.ProjectRoots,
            Model = source.Model,
            ReasoningEffort = source.ReasoningEffort,
            Tools = augmentation.Tools ?? source.Tools,
            AdditionalSystemMessage = AppendPromptText(source.AdditionalSystemMessage, augmentation.AdditionalSystemMessage),
            AdditionalDeveloperInstructions = AppendPromptText(source.AdditionalDeveloperInstructions, augmentation.AdditionalDeveloperInstructions),
            PreferredToolNames = source.PreferredToolNames.Concat(augmentation.PreferredToolNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            OnPermissionRequest = source.OnPermissionRequest,
            OnUserInputRequest = source.OnUserInputRequest,
        };

    private static string? AppendPromptText(string? existing, string? additional)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return string.IsNullOrWhiteSpace(additional) ? null : additional;
        }

        if (string.IsNullOrWhiteSpace(additional))
        {
            return existing;
        }

        return existing.TrimEnd() + "\n\n" + additional.Trim();
    }

    private static bool IsCodeAltaManagedProvider(ModelProviderId providerId)
        => !string.Equals(providerId.Value, ModelProviderIds.Codex.Value, StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(providerId.Value, ModelProviderIds.Copilot.Value, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<AgentToolDefinition>? CreateAltaSessionTools(
        AltaCommandContext context,
        string ProviderId,
        Func<string?>? sourceSessionIdProvider,
        string? sourceProjectId,
        string? workingDirectory)
    {
        var policy = context.Services.Get<IAltaSessionToolProviderPolicy>();
        if (policy is null || !policy.SupportsAltaSessionTool(ProviderId))
        {
            return null;
        }

        var dispatcher = context.Services.Get<AltaCommandDispatcher>()
            ?? new AltaCommandDispatcher(new AltaCommandRegistry(), context.Services);
        return
        [
            AltaSessionToolFactory.Create(
                dispatcher,
                new AltaSessionToolOptions
                {
                    SourceSessionIdProvider = sourceSessionIdProvider,
                    SourceProjectId = sourceProjectId,
                    WorkingDirectory = workingDirectory,
                    DefaultMaxOutputRecords = 200,
                    DefaultMaxOutputBytes = 64 * 1024,
                    DefaultTimeout = TimeSpan.FromSeconds(120),
                }),
        ];
    }

    private static async Task<ModelResolutionResult> ResolveModelSelectionAsync(AltaCommandContext context, AltaModelSelectionOptions request, string commandPath = "alta model resolve")
    {
        if (!string.IsNullOrWhiteSpace(request.ModelRef))
        {
            if (!AltaModelRef.TryParse(request.ModelRef, out var parsed, out var error))
            {
                return ModelResolutionResult.Fail(UsageError(context, "usage.invalidModelRef", error!, commandPath));
            }

            return await ValidateAndCompleteModelSelectionAsync(context, parsed, requestedReasoning: parsed.ReasoningEffort, commandPath).ConfigureAwait(false);
        }

        AltaModelSelection? inherited = null;
        if (!string.IsNullOrWhiteSpace(request.SameModelAsSessionId))
        {
            var inheritedResult = await ResolveSessionModelSelectionAsync(context, request.SameModelAsSessionId).ConfigureAwait(false);
            if (inheritedResult.ExitCode != AltaExitCodes.Success)
            {
                return ModelResolutionResult.Fail(inheritedResult.ExitCode);
            }

            inherited = inheritedResult.Selection;
            if (!inheritedResult.Found)
            {
                return ModelResolutionResult.Fail(NotFound(context, "session.notFound", $"Session '{request.SameModelAsSessionId}' was not found."));
            }
        }
        else if (!HasCompleteModelSelection(request) && !string.IsNullOrWhiteSpace(context.Caller.SourceSessionId))
        {
            var inheritedResult = await ResolveSessionModelSelectionAsync(context, context.Caller.SourceSessionId).ConfigureAwait(false);
            if (inheritedResult.ExitCode == AltaExitCodes.Success)
            {
                inherited = inheritedResult.Selection;
            }
        }
        var providerKey = FirstNonEmpty(request.ProviderKey, inherited?.ProviderKey, GetDefaultProviderKey(context));
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return ModelResolutionResult.Fail(NotFound(context, "provider.notFound", "No provider is registered or selected."));
        }

        var modelId = FirstNonEmpty(request.ModelId, inherited?.ModelId);
        var reasoning = request.ReasoningEffort ?? inherited?.ReasoningEffort;
        var selection = new AltaModelSelection
        {
            ProviderKey = providerKey,
            ModelId = modelId,
            ReasoningEffort = reasoning,
            ModelRef = AltaModelRef.Format(providerKey, modelId, reasoning),
        };
        return await ValidateAndCompleteModelSelectionAsync(context, selection, request.ReasoningEffort, commandPath).ConfigureAwait(false);
    }

    private static bool HasCompleteModelSelection(AltaModelSelectionOptions request)
        => !string.IsNullOrWhiteSpace(request.ProviderKey) &&
           !string.IsNullOrWhiteSpace(request.ModelId) &&
           request.ReasoningEffort is not null;

    private static async Task<ModelResolutionResult> ValidateAndCompleteModelSelectionAsync(AltaCommandContext context, AltaModelSelection selection, AgentReasoningEffort? requestedReasoning, string commandPath)
    {
        if (context.Services.Get<IModelProviderInitializationService>() is not { } providerInitializationService || string.IsNullOrWhiteSpace(selection.ModelId))
        {
            return ModelResolutionResult.Success(selection);
        }

        IReadOnlyList<AgentModelInfo> models;
        try
        {
            models = await providerInitializationService.GetModelsAsync(new ModelProviderId(selection.ProviderKey), context.CancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return ModelResolutionResult.Fail(NotFound(context, "provider.notFound", $"Provider '{selection.ProviderKey}' was not found."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            AltaJsonlWriter.WriteWarning(context.Stderr, context.CorrelationId, "model.validationUnavailable", $"Provider '{selection.ProviderKey}' models are unavailable: {ex.Message}");
            return ModelResolutionResult.Success(selection);
        }

        var model = models.FirstOrDefault(candidate => string.Equals(candidate.Id, selection.ModelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return ModelResolutionResult.Fail(NotFound(context, "model.notFound", $"Model '{selection.ModelId}' was not found for provider '{selection.ProviderKey}'."));
        }

        if (requestedReasoning is { } requested && !ModelSupportsReasoning(model, requested))
        {
            return ModelResolutionResult.Fail(UsageError(context, "usage.unsupportedReasoning", $"Reasoning effort '{AltaModelRef.ToWireName(requested)}' is not supported by model '{selection.ModelId}' for provider '{selection.ProviderKey}'.", commandPath));
        }

        var effectiveReasoning = selection.ReasoningEffort ?? model.DefaultReasoningEffort;
        return ModelResolutionResult.Success(selection with
        {
            ModelId = model.Id,
            ReasoningEffort = effectiveReasoning,
            ModelRef = AltaModelRef.Format(selection.ProviderKey, model.Id, effectiveReasoning),
        });
    }

    private static async Task<SessionModelSelectionResult> ResolveSessionModelSelectionAsync(AltaCommandContext context, string sessionId)
    {
        if (context.Services.Get<SessionRuntimeService>() is not { } runtime)
        {
            return SessionModelSelectionResult.NotFound();
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SessionViewDescriptor? session = null;
        AltaModelSelection? selection = null;
        var currentSessionId = sessionId;
        while (!string.IsNullOrWhiteSpace(currentSessionId) && visited.Add(currentSessionId))
        {
            session = await runtime.TryGetActiveSessionDescriptorAsync(currentSessionId, context.CancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                break;
            }

            var candidate = CreateModelSelection(session, preference: null);
            selection = MergeModelSelection(selection, candidate);
            if (!string.IsNullOrWhiteSpace(selection.ProviderKey) &&
                !string.IsNullOrWhiteSpace(selection.ModelId) &&
                selection.ReasoningEffort is not null)
            {
                break;
            }

            currentSessionId = session.ParentSessionId;
        }

        return selection is null
            ? SessionModelSelectionResult.NotFound()
            : SessionModelSelectionResult.Success(selection);
    }

    private static string? GetDefaultProviderKey(AltaCommandContext context)
    {
        if (GetProviderDescriptors(context).FirstOrDefault() is { } descriptor)
        {
            return descriptor.ProviderId.Value;
        }

        return null;
    }

    private static IReadOnlyList<ModelProviderDescriptor> GetProviderDescriptors(AltaCommandContext context)
        => context.Services.Get<IReadOnlyList<ModelProviderDescriptor>>() ?? [];

    private static AltaModelSelection CreateModelSelection(SessionViewDescriptor session, SessionViewPreference? preference)
    {
        var providerKey = session.ResolvedProviderKey;
        var modelId = preference?.ModelId ?? session.ModelId;
        var reasoning = preference?.ReasoningEffort ?? session.ReasoningEffort;
        return new AltaModelSelection
        {
            ProviderKey = providerKey,
            ModelId = modelId,
            ReasoningEffort = reasoning,
            ModelRef = AltaModelRef.Format(providerKey, modelId, reasoning),
        };
    }

    private static AltaModelSelection MergeModelSelection(AltaModelSelection? primary, AltaModelSelection fallback)
    {
        if (primary is null)
        {
            return fallback;
        }

        var providerKey = FirstNonEmpty(primary.ProviderKey, fallback.ProviderKey) ?? fallback.ProviderKey;
        var modelId = FirstNonEmpty(primary.ModelId, fallback.ModelId);
        var reasoning = primary.ReasoningEffort ?? fallback.ReasoningEffort;
        return new AltaModelSelection
        {
            ProviderKey = providerKey,
            ModelId = modelId,
            ReasoningEffort = reasoning,
            ModelRef = AltaModelRef.Format(providerKey, modelId, reasoning),
        };
    }

    private static async Task<SkillQueryResult> BuildSkillQueryAsync(AltaCommandContext context, string? projectRef)
    {
        ProjectDescriptor? project = null;
        var sourceProjectId = context.Caller.SourceProjectId;
        if (!string.IsNullOrWhiteSpace(projectRef))
        {
            if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
            {
                return SkillQueryResult.Fail(AltaExitCodes.ServiceUnavailable);
            }

            project = await ResolveProjectAsync(catalog, projectRef, context, includeArchived: false).ConfigureAwait(false);
            if (project is null)
            {
                return SkillQueryResult.Fail(NotFound(context, "project.notFound", $"Project '{projectRef}' was not found."));
            }
        }
        else if (!string.IsNullOrWhiteSpace(sourceProjectId))
        {
            if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
            {
                return SkillQueryResult.Fail(AltaExitCodes.ServiceUnavailable);
            }

            project = await ResolveProjectAsync(catalog, sourceProjectId, context, includeArchived: false).ConfigureAwait(false);
            if (project is null)
            {
                return SkillQueryResult.Fail(NotFound(context, "project.notFound", $"Project '{sourceProjectId}' was not found."));
            }
        }

        var roots = project is null ? [] : new[] { project.ProjectPath };
        var query = new SkillCatalogQuery
        {
            Discovery = new SkillDiscoveryContext
            {
                ProjectRoots = roots,
                UserCodeAltaRoot = context.Services.Get<CatalogOptions>()?.GlobalRoot ?? GetGlobalRootOrCwd(context),
                UserProfileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            },
            IncludeInvalid = true,
            IncludeShadowed = true,
            IncludeUntrusted = true,
        };
        return SkillQueryResult.Success(query);
    }

    private static async Task<PromptQueryResult> BuildPromptQueryAsync(AltaCommandContext context, string? projectRef)
    {
        var projectRoot = await ResolvePromptProjectRootAsync(context, projectRef).ConfigureAwait(false);
        if (projectRoot.ExitCode != AltaExitCodes.Success)
        {
            return PromptQueryResult.Fail(projectRoot.ExitCode);
        }

        return PromptQueryResult.Success(new UserPromptCatalogQuery
        {
            ProjectRoot = projectRoot.ProjectRoot,
            ProjectPromptResourcesTrusted = !string.IsNullOrWhiteSpace(projectRoot.ProjectRoot),
            UserCodeAltaRoot = context.Services.Get<CatalogOptions>()?.GlobalRoot ?? GetGlobalRootOrCwd(context),
            UserProfileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        });
    }

    private static async Task<PromptProjectRootResult> ResolvePromptProjectRootAsync(AltaCommandContext context, string? projectRef)
    {
        if (!string.IsNullOrWhiteSpace(projectRef))
        {
            if (context.Services.Get<ProjectCatalog>() is { } catalog)
            {
                var project = await ResolveProjectAsync(catalog, projectRef, context, includeArchived: false).ConfigureAwait(false);
                if (project is not null)
                {
                    return PromptProjectRootResult.Success(project.ProjectPath);
                }
            }

            var path = ResolvePath(context, projectRef);
            return Directory.Exists(path)
                ? PromptProjectRootResult.Success(path)
                : PromptProjectRootResult.Fail(NotFound(context, "project.notFound", $"Project '{projectRef}' was not found."));
        }

        if (!string.IsNullOrWhiteSpace(context.Caller.SourceProjectId) && context.Services.Get<ProjectCatalog>() is { } sourceCatalog)
        {
            var project = await ResolveProjectAsync(sourceCatalog, context.Caller.SourceProjectId, context, includeArchived: false).ConfigureAwait(false);
            if (project is not null)
            {
                return PromptProjectRootResult.Success(project.ProjectPath);
            }
        }

        var cwd = context.Cwd ?? Environment.CurrentDirectory;
        if (context.Services.Get<ProjectCatalog>() is { } cwdCatalog)
        {
            var projects = await cwdCatalog.LoadAsync(context.CancellationToken).ConfigureAwait(false);
            var normalizedCwd = NormalizePath(cwd);
            var project = projects
                .Where(static project => !project.Archived)
                .OrderByDescending(static project => NormalizePath(project.ProjectPath).Length)
                .FirstOrDefault(project => IsSameOrDescendantPath(normalizedCwd, NormalizePath(project.ProjectPath)));
            if (project is not null)
            {
                return PromptProjectRootResult.Success(project.ProjectPath);
            }
        }

        return Directory.Exists(cwd) ? PromptProjectRootResult.Success(cwd) : PromptProjectRootResult.Success(null);
    }

    private static bool IsSameOrDescendantPath(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rooted = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(rooted, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> ValidateSessionUserPromptIdAsync(AltaCommandContext context, AltaSessionInfo info, string? promptId)
    {
        var queryResult = await BuildPromptQueryAsync(context, info.Session.ProjectRef).ConfigureAwait(false);
        if (queryResult.ExitCode != AltaExitCodes.Success)
        {
            return queryResult.ExitCode;
        }

        var normalized = NormalizeOptionalText(promptId);
        if (normalized is null)
        {
            return AltaExitCodes.Success;
        }

        var exists = new UserPromptCatalog()
            .ListEffectivePrompts(queryResult.Query!)
            .Any(prompt => string.Equals(prompt.PromptName, normalized, StringComparison.OrdinalIgnoreCase));
        return exists
            ? AltaExitCodes.Success
            : NotFound(context, "prompt.notFound", $"User prompt '{normalized}' was not found. Use `alta prompt list` to discover prompt ids.");
    }

    private static Dictionary<string, object?> CreateUserPromptRecord(AltaCommandContext context, UserPromptDescriptor prompt, bool includeContent)
    {
        var record = CreatePromptRecord(context, prompt.PromptName, prompt.DisplayName, prompt.Description, "user", prompt.SourceKind, prompt.SourcePath, prompt.ContentHash, prompt.IsShadowed, prompt.ShadowedByPath);
        record["systemPromptId"] = prompt.SystemPromptName;
        if (includeContent)
        {
            record["content"] = prompt.Body;
        }

        return record;
    }

    private static Dictionary<string, object?> CreateSystemPromptRecord(AltaCommandContext context, SystemPromptDescriptor prompt, bool includeContent)
    {
        var metadata = ReadPromptMetadata(prompt.SourcePath);
        var record = CreatePromptRecord(context, prompt.PromptName, metadata.Name ?? prompt.PromptName, metadata.Description, "system", prompt.SourceKind, prompt.SourcePath, prompt.ContentHash, prompt.IsShadowed, prompt.ShadowedByPath);
        if (includeContent)
        {
            record["content"] = prompt.Body;
        }

        return record;
    }

    private static Dictionary<string, object?> CreatePromptRecord(AltaCommandContext context, string promptId, string name, string? description, string promptKind, UserPromptSourceKind sourceKind, string sourcePath, string contentHash, bool isShadowed, string? shadowedByPath)
        => new(StringComparer.Ordinal)
        {
            ["type"] = "alta.prompt",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["promptId"] = promptId,
            ["id"] = promptId,
            ["name"] = name,
            ["description"] = description,
            ["promptKind"] = promptKind,
            ["source"] = ToPromptSourceLabel(sourceKind),
            ["scope"] = ToPromptScopeLabel(sourceKind),
            ["path"] = sourcePath,
            ["contentHash"] = contentHash,
            ["shadowed"] = isShadowed,
            ["shadowedByPath"] = shadowedByPath,
        };

    private static PromptMetadata ReadPromptMetadata(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            {
                return PromptMetadata.Empty;
            }

            var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end < 0)
            {
                return PromptMetadata.Empty;
            }

            string? name = null;
            string? description = null;
            foreach (var rawLine in normalized[4..end].Split('\n'))
            {
                var line = rawLine.Trim();
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    continue;
                }

                var key = line[..colon].Trim();
                var value = NormalizeOptionalText(line[(colon + 1)..].Trim().Trim('"', '\''));
                if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                {
                    name = value;
                }
                else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
                {
                    description = value;
                }
            }

            return new PromptMetadata(name, description);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PromptMetadata.Empty;
        }
    }

    private static string BuildUserPromptCreateFile(string name, string? description, string systemPromptId, string body)
    {
        var lines = new List<string>
        {
            "---",
            "name: " + ToYamlScalar(name),
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            lines.Add("description: " + ToYamlScalar(description!));
        }

        if (!string.Equals(systemPromptId, UserPromptCatalog.DefaultPromptName, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("system: " + ToYamlScalar(systemPromptId));
        }

        lines.Add("---");
        lines.Add(body.Trim());
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string BuildSystemPromptCreateFile(string body, string? name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(description))
        {
            return body.Trim() + Environment.NewLine;
        }

        var lines = new List<string> { "---" };
        if (!string.IsNullOrWhiteSpace(name))
        {
            lines.Add("name: " + ToYamlScalar(name!));
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            lines.Add("description: " + ToYamlScalar(description!));
        }

        lines.Add("---");
        lines.Add(body.Trim());
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string ToYamlScalar(string value)
    {
        var mustQuote = value.Length == 0 ||
            char.IsWhiteSpace(value[0]) ||
            char.IsWhiteSpace(value[^1]) ||
            value.Any(static ch => ch is ':' or '#' or '\'' or '"' or '[' or ']' or '{' or '}' or ',');
        return mustQuote
            ? '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
            : value;
    }

    private static bool ScopeMatches(string? scope, UserPromptSourceKind sourceKind)
        => (scope ?? "all") switch
        {
            "all" => true,
            "builtin" => sourceKind == UserPromptSourceKind.BuiltIn,
            "global" => sourceKind == UserPromptSourceKind.UserGlobal,
            "project" => sourceKind == UserPromptSourceKind.Project,
            _ => true,
        };

    private static TPrompt? SelectPromptForShow<TPrompt>(IEnumerable<TPrompt> prompts, string promptId, string? scope)
        where TPrompt : class
    {
        var matches = prompts
            .Where(prompt => string.Equals(GetPromptName(prompt), promptId, StringComparison.OrdinalIgnoreCase) && ScopeMatches(scope, GetPromptSourceKind(prompt)))
            .OrderBy(prompt => GetPromptPrecedence(prompt))
            .ToArray();
        return (scope ?? "all") == "all"
            ? matches.LastOrDefault(prompt => !GetPromptIsShadowed(prompt)) ?? matches.LastOrDefault()
            : matches.LastOrDefault();
    }

    private static string GetPromptName<TPrompt>(TPrompt prompt)
        => prompt switch
        {
            UserPromptDescriptor userPrompt => userPrompt.PromptName,
            SystemPromptDescriptor systemPrompt => systemPrompt.PromptName,
            _ => string.Empty,
        };

    private static UserPromptSourceKind GetPromptSourceKind<TPrompt>(TPrompt prompt)
        => prompt switch
        {
            UserPromptDescriptor userPrompt => userPrompt.SourceKind,
            SystemPromptDescriptor systemPrompt => systemPrompt.SourceKind,
            _ => UserPromptSourceKind.BuiltIn,
        };

    private static int GetPromptPrecedence<TPrompt>(TPrompt prompt)
        => prompt switch
        {
            UserPromptDescriptor userPrompt => userPrompt.Precedence,
            SystemPromptDescriptor systemPrompt => systemPrompt.Precedence,
            _ => 0,
        };

    private static bool GetPromptIsShadowed<TPrompt>(TPrompt prompt)
        => prompt switch
        {
            UserPromptDescriptor userPrompt => userPrompt.IsShadowed,
            SystemPromptDescriptor systemPrompt => systemPrompt.IsShadowed,
            _ => false,
        };

    private static string ToPromptSourceLabel(UserPromptSourceKind sourceKind)
        => sourceKind switch
        {
            UserPromptSourceKind.BuiltIn => "built-in",
            UserPromptSourceKind.UserGlobal => "user-global",
            UserPromptSourceKind.Project => "project",
            _ => sourceKind.ToString(),
        };

    private static string ToPromptScopeLabel(UserPromptSourceKind sourceKind)
        => sourceKind switch
        {
            UserPromptSourceKind.BuiltIn => "builtin",
            UserPromptSourceKind.UserGlobal => "global",
            UserPromptSourceKind.Project => "project",
            _ => sourceKind.ToString().ToLowerInvariant(),
        };

    private static string? AppendPromptSubdirectory(string? root, string subdirectory)
        => string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, subdirectory);

    private static bool IsSafePromptFileStem(string promptId)
    {
        if (string.IsNullOrWhiteSpace(promptId))
        {
            return false;
        }

        var trimmed = promptId.Trim();
        return Path.GetFileName(trimmed) == trimmed &&
               !trimmed.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !trimmed.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal) &&
               !string.Equals(trimmed, ".", StringComparison.Ordinal) &&
               !string.Equals(trimmed, "..", StringComparison.Ordinal) &&
               !trimmed.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.EndsWith(".system-prompt.md", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<ProjectDescriptor?> ResolveProjectAsync(ProjectCatalog catalog, string? reference, AltaCommandContext context, bool includeArchived)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var projects = await catalog.LoadAsync(context.CancellationToken).ConfigureAwait(false);
        var candidates = includeArchived ? projects : projects.Where(static project => !project.Archived).ToArray();
        var trimmed = reference.Trim();
        var project = candidates.FirstOrDefault(project => string.Equals(project.Id, trimmed, StringComparison.OrdinalIgnoreCase)) ??
                      candidates.FirstOrDefault(project => string.Equals(project.Slug, trimmed, StringComparison.OrdinalIgnoreCase));
        if (project is not null)
        {
            return project;
        }

        try
        {
            var path = ResolvePath(context, trimmed);
            return candidates.FirstOrDefault(project => string.Equals(NormalizePath(project.ProjectPath), NormalizePath(path), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) when (!LooksLikePath(trimmed))
        {
            return null;
        }
    }

    private static bool LooksLikePath(string value)
        => Path.IsPathRooted(value) ||
           value.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
           value.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
           value is "." or ".." ||
           value.StartsWith(".", StringComparison.Ordinal);

    private static string ResolvePath(AltaCommandContext context, string path)
    {
        var basePath = context.Cwd;
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = Environment.CurrentDirectory;
        }

        return Path.IsPathRooted(path)
            ? NormalizePath(path)
            : NormalizePath(Path.Combine(basePath, path));
    }

    private static string NormalizePath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = @"\\" + trimmed[8..];
        }
        else if (trimmed.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[4..];
        }

        return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static async Task<PromptReadResult> ReadPromptAsync(AltaCommandContext context, PromptOptions options, PromptDispatchKind kind)
    {
        if (!string.IsNullOrWhiteSpace(options.Message) && options.UseStdin)
        {
            return PromptReadResult.Fail(UsageError(context, "usage.messageConflict", "Use either --message or --stdin, not both.", CommandPathForPrompt(kind)));
        }

        if (!string.IsNullOrWhiteSpace(options.Message))
        {
            return PromptReadResult.Success(options.Message);
        }

        if (options.UseStdin)
        {
            return PromptReadResult.Success(await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false));
        }

        return PromptReadResult.Fail(UsageError(context, "usage.missingMessage", "A message is required. Use --message <text> or --stdin.", CommandPathForPrompt(kind)));
    }

    private static async Task<PromptReadResult> ReadReminderContentAsync(AltaCommandContext context, ReminderCreateOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Content) && options.UseStdin)
        {
            return PromptReadResult.Fail(UsageError(context, "usage.contentConflict", "Use either --content or --stdin, not both.", "alta reminder create"));
        }

        if (!string.IsNullOrWhiteSpace(options.Content))
        {
            return PromptReadResult.Success(options.Content);
        }

        if (options.UseStdin)
        {
            var content = await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(content)
                ? PromptReadResult.Fail(UsageError(context, "usage.missingContent", "Reminder content is required. Provide non-empty --content text or non-empty --stdin input.", "alta reminder create"))
                : PromptReadResult.Success(content);
        }

        return PromptReadResult.Fail(UsageError(context, "usage.missingContent", "Reminder content is required. Use --content <text> or --stdin.", "alta reminder create"));
    }

    private static bool TryParseReminderDuration(string? value, out TimeSpan duration, out string error)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "--duration is required.";
            return false;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            if (seconds > 0 && !double.IsInfinity(seconds) && !double.IsNaN(seconds))
            {
                try
                {
                    duration = TimeSpan.FromSeconds(seconds);
                    error = string.Empty;
                    return true;
                }
                catch (OverflowException)
                {
                    error = "--duration is too large.";
                    return false;
                }
            }

            error = "--duration must be greater than zero seconds.";
            return false;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed > TimeSpan.Zero)
        {
            duration = parsed;
            error = string.Empty;
            return true;
        }

        error = "--duration must be a positive number of seconds or a positive TimeSpan such as 00:01:00.";
        return false;
    }

    private static bool TryParseReminderRepeat(string? value, out int repeat, out string error)
    {
        repeat = 1;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = string.Empty;
            return true;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out repeat) && repeat > 0)
        {
            error = string.Empty;
            return true;
        }

        repeat = 1;
        error = "--repeat must be a positive integer.";
        return false;
    }

    private static string CommandPathForPrompt(PromptDispatchKind kind)
        => kind switch
        {
            PromptDispatchKind.Steer => "alta session steer",
            PromptDispatchKind.Queue => "alta session queue",
            PromptDispatchKind.Message => "alta session message",
            PromptDispatchKind.Request => "alta session request",
            _ => "alta session send",
        };

    private static async Task PersistSessionPreferenceAsync(AltaCommandContext context, SessionViewDescriptor session, AltaModelSelection selection)
    {
        if (context.Services.Get<SessionViewCatalog>() is not { } sessionCatalog)
        {
            return;
        }

        var state = await sessionCatalog.JournalStore
            .ReadLatestStateAsync(session.SessionId, session.CreatedAt, context.CancellationToken)
            .ConfigureAwait(false) ?? new SessionViewLocalState();
        state.ProviderKey = selection.ProviderKey;
        state.ModelId = selection.ModelId;
        state.ReasoningEffort = selection.ReasoningEffort;
        await sessionCatalog.JournalStore.AppendStateAsync(session, state, context.CancellationToken).ConfigureAwait(false);
    }

    private static async Task<SessionViewLocalState?> TryReadLatestSessionStateAsync(
        SessionViewCatalog sessionCatalog,
        SessionViewDescriptor session,
        CancellationToken cancellationToken)
    {
        try
        {
            return await sessionCatalog.JournalStore.ReadLatestStateAsync(session.SessionId, session.CreatedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task PersistPromptProvenanceAsync(
        AltaCommandContext context,
        SessionViewDescriptor session,
        string? runId,
        bool queued,
        PromptDispatchKind kind,
        string prompt)
    {
        if (context.Services.Get<SessionViewCatalog>() is not { } sessionCatalog)
        {
            return;
        }

        SessionViewLocalState? localState = null;
        try
        {
            localState = await sessionCatalog.JournalStore
                .ReadLatestStateAsync(session.SessionId, session.CreatedAt, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {
        }

        localState ??= new SessionViewLocalState();
        localState.ProviderKey = session.ResolvedProviderKey;
        localState.ModelId = session.ModelId;
        localState.ReasoningEffort = session.ReasoningEffort;
        localState.Archived = session.Status == SessionViewStatus.Archived;
        localState.MessageCount = session.MessageCount;
        localState.ParentSessionId = session.ParentSessionId;
        localState.CreatedBy = session.CreatedBy;
        localState.PromptProvenance ??= [];
        localState.PromptProvenance.Add(new SessionViewPromptProvenance
        {
            PromptId = "prompt-" + Guid.NewGuid().ToString("N"),
            Kind = kind.ToString().ToLowerInvariant(),
            RunId = runId,
            Queued = queued,
            PromptPreview = prompt.Length <= 160 ? prompt : prompt[..160],
            SubmittedBy = CreateProvenance(context),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        const int MaxPromptProvenanceRecords = 200;
        if (localState.PromptProvenance.Count > MaxPromptProvenanceRecords)
        {
            localState.PromptProvenance.RemoveRange(0, localState.PromptProvenance.Count - MaxPromptProvenanceRecords);
        }

        await sessionCatalog.JournalStore.AppendStateAsync(session, localState, context.CancellationToken).ConfigureAwait(false);
    }

    private static bool IsAgentCaller(AltaCommandContext context)
        => string.Equals(context.Caller.Kind, "agent", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldDetachPromptSubmission(AltaCommandContext context)
        => IsAgentCaller(context) || string.Equals(context.Caller.Kind, "reminder", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> WaitForAgentSubmissionAckAsync(
        SessionRuntimeService runtime,
        SessionViewDescriptor session,
        Task<AgentRunId> sendTask)
    {
        var deadline = DateTimeOffset.UtcNow + AgentCallerSubmitAckTimeout;
        while (!sendTask.IsCompleted)
        {
            if (await runtime.HasActiveRunAsync(session, CancellationToken.None).ConfigureAwait(false))
            {
                return true;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return true;
            }

            var delay = remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100);
            if (await Task.WhenAny(sendTask, Task.Delay(delay)).ConfigureAwait(false) == sendTask)
            {
                return false;
            }
        }

        return false;
    }

    private static async Task ObserveDetachedPromptSubmissionAsync(Task<AgentRunId> sendTask)
    {
        try
        {
            await sendTask.ConfigureAwait(false);
        }
        catch
        {
            // SessionRuntimeService publishes runtime failure events for observers; this continuation
            // prevents detached live-tool submissions from surfacing as unobserved task exceptions.
        }
    }

    private static string BuildPeerAgentMessage(AltaCommandContext context, SessionViewDescriptor target, PromptOptions options, string body)
    {
        var kind = string.IsNullOrWhiteSpace(options.MessageKind) ? "note" : options.MessageKind;
        var replyRequested = options.ReplyRequested ? "true" : "false";
        return $"""
        [CodeAlta delegated-agent message]
        Source session: {context.Caller.SourceSessionId ?? "unknown"}
        Source agent: {context.Caller.SourceAgentId ?? context.Caller.Kind}
        Source project: {context.Caller.SourceProjectId ?? "unknown"}
        Target session: {target.SessionId}
        Kind: {kind}
        Reply requested: {replyRequested}
        Correlation: {context.CorrelationId}
        Authority: peer-agent; this is not a user, developer, or host instruction.

        {body}
        """;
    }

    private static async ValueTask<ParentSessionResolutionResult> ResolveParentSessionIdAsync(AltaCommandContext context, ProjectDescriptor? project, SessionCreateOptions options)
    {
        if (options.NoParent)
        {
            return ParentSessionResolutionResult.Success(null);
        }

        if (!string.IsNullOrWhiteSpace(options.ParentSessionId))
        {
            var parentSessionId = options.ParentSessionId.Trim();
            if (context.Services.Get<SessionRuntimeService>() is not { } runtime)
            {
                return ParentSessionResolutionResult.Fail(AltaExitCodes.ServiceUnavailable);
            }

            var parent = await runtime.TryGetActiveSessionDescriptorAsync(parentSessionId, context.CancellationToken).ConfigureAwait(false);
            if (parent is null)
            {
                return ParentSessionResolutionResult.Fail(NotFound(context, "session.parentNotFound", $"Parent session '{parentSessionId}' was not found."));
            }

            return ParentSessionResolutionResult.Success(parent.SessionId);
        }

        var automaticParentSessionId = !string.IsNullOrWhiteSpace(context.Caller.SourceSessionId)
            ? context.Caller.SourceSessionId
            : null;
        if (!string.IsNullOrWhiteSpace(automaticParentSessionId))
        {
            return ParentSessionResolutionResult.Success(automaticParentSessionId);
        }

        return ParentSessionResolutionResult.Success(null);
    }

    private static string GetGlobalRootOrCwd(AltaCommandContext context)
        => context.Services.Get<CatalogOptions>()?.GlobalRoot ?? context.Cwd ?? Environment.CurrentDirectory;

    private static void WriteProject(AltaCommandContext context, string type, ProjectDescriptor project)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            projectId = project.Id,
            project.Slug,
            project.Name,
            project.DisplayName,
            projectPath = project.ProjectPath,
            project.DefaultBranch,
            project.Archived,
            project.SourcePath,
            project.Description,
            tags = project.Tags,
        });
    }

    private static void WriteProjectRefs(AltaCommandContext context, IReadOnlyList<ProjectDescriptor> projects)
        => AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.project.refs",
            projects = projects.Select(static project => new[] { project.Slug, project.ProjectPath }).ToArray(),
        });

    private static void WriteSession(AltaCommandContext context, string type, AltaSessionInfo info, bool includeChildren, IReadOnlyList<AltaSessionInfo> infos, SessionMetrics? metrics = null)
    {
        var children = includeChildren ? GetChildren(infos, info, recursive: false).ToArray() : [];
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = info.Session.SessionId,
            kind = SessionKindWire(info.Session.Kind),
            ProviderId = info.Session.ProviderId,
            providerKey = info.Session.ResolvedProviderKey,
            projectId = info.Session.ProjectRef,
            projectRef = info.Session.ProjectRef,
            parentSessionId = info.Session.ParentSessionId,
            createdBy = info.Session.CreatedBy,
            title = info.Session.Title,
            state = info.State,
            status = SessionStatusWire(info.Session.Status),
            workingDirectory = info.Session.WorkingDirectory,
            latestSummary = info.Session.LatestSummary,
            messageCount = info.Session.MessageCount,
            isRunning = info.IsRunning,
            queuedPromptCount = info.LocalState?.QueuedPrompts.Count(static prompt => IsPendingQueuedPromptState(prompt.State)) ?? 0,
            modelSelection = ToModelSelectionPayload(CreateModelSelection(info.Session, info.Preference)),
            createdAt = info.Session.CreatedAt,
            updatedAt = info.Session.UpdatedAt,
            lastActiveAt = info.Session.LastActiveAt,
            startedAt = info.Session.StartedAt,
            sourcePath = info.Session.SourcePath,
            metrics = metrics is null ? null : ToCompactMetricsPayload(metrics),
            childCount = includeChildren ? children.Length : (int?)null,
            childSessionIds = includeChildren ? children.Select(static child => child.Session.SessionId).ToArray() : null,
        });
    }

    private static void WriteSessionMetrics(AltaCommandContext context, string type, AltaSessionInfo info, SessionMetrics metrics)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = info.Session.SessionId,
            ProviderId = info.Session.ProviderId,
            providerKey = info.Session.ResolvedProviderKey,
            metrics = ToDetailedMetricsPayload(metrics),
        });
    }

    private static void WriteSessionResult(AltaCommandContext context, string type, AltaSessionInfo info, SessionResult result, bool includeResult, bool includeMetrics)
    {
        var finalAnswer = !includeResult || result.FinalAnswer is null
            ? null
            : new
            {
                text = result.FinalAnswer,
                characters = result.FinalAnswer.Length,
                words = CountWords(result.FinalAnswer),
                estimatedTokens = TokenEstimator.Estimate(result.FinalAnswer),
                completedAt = result.FinalAssistantAt,
            };

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = info.Session.SessionId,
            ProviderId = info.Session.ProviderId,
            providerKey = info.Session.ResolvedProviderKey,
            status = StatusWire(result.Status),
            scope = MetricsScopeWire(result.Scope),
            startedAt = result.Metrics.StartedAt,
            completedAt = result.Metrics.CompletedAt,
            durationMs = ToMilliseconds(result.Metrics.Duration),
            toolCallCount = result.Metrics.ToolCallCount,
            modelSelection = result.Metrics.ModelSelection,
            finalAnswer,
            finalError = includeResult ? result.FinalError : null,
            metrics = includeMetrics ? ToDetailedMetricsPayload(result.Metrics) : null,
        });
    }

    private static void WriteSessionReportDiagnostic(AltaCommandContext context, string requestedSessionId, string code, int exitCode, string message)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.report.item",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = requestedSessionId,
            status = "not_found",
            diagnostic = new
            {
                code,
                exitCode,
                message,
            },
        });
    }

    private static void WriteSessionReportSummary(AltaCommandContext context, int count, int successCount, int diagnosticCount, bool truncated)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.report.summary",
            version = 1,
            correlationId = context.CorrelationId,
            count,
            successCount,
            diagnosticCount,
            truncated,
        });
    }

    private static object ToCompactMetricsPayload(SessionMetrics metrics)
        => new
        {
            scope = MetricsScopeWire(metrics.Scope),
            metrics.Status,
            metrics.EventCount,
            metrics.StartedAt,
            metrics.CompletedAt,
            durationMs = ToMilliseconds(metrics.Duration),
            metrics.ToolCallCount,
            finalAnswer = new
            {
                metrics.FinalAnswerCharacters,
                metrics.FinalAnswerWords,
                estimatedTokens = metrics.FinalAnswerEstimatedTokens,
            },
            usage = ToUsagePayload(metrics.FinalUsage),
            providerOperations = ToProviderOperationTotalsPayload(metrics.ProviderOperations),
            finalError = metrics.FinalError,
        };

    private static object ToDetailedMetricsPayload(SessionMetrics metrics)
        => new
        {
            scope = MetricsScopeWire(metrics.Scope),
            metrics.Status,
            metrics.EventCount,
            metrics.RunCount,
            metrics.StartedAt,
            metrics.CompletedAt,
            durationMs = ToMilliseconds(metrics.Duration),
            metrics.FinalAssistantAt,
            metrics.UserMessageCount,
            metrics.AssistantMessageCount,
            metrics.ToolCallCount,
            finalAnswer = new
            {
                metrics.FinalAnswerCharacters,
                metrics.FinalAnswerWords,
                estimatedTokens = metrics.FinalAnswerEstimatedTokens,
            },
            currentUsage = ToUsagePayload(metrics.FinalUsage),
            providerOperations = ToProviderOperationTotalsPayload(metrics.ProviderOperations),
            finalError = metrics.FinalError,
            metrics.ModelSelection,
        };

    private static object? ToUsagePayload(AgentSessionUsage? usage)
        => usage is null ? null : new
        {
            window = usage.Window is null ? null : new
            {
                usage.Window.CurrentTokens,
                usage.Window.TokenLimit,
                usage.Window.MessageCount,
                usage.Window.Label,
                usage.WindowUsagePercentage,
            },
            lastOperation = usage.LastOperation is null ? null : new
            {
                usage.LastOperation.Model,
                usage.LastOperation.InputTokens,
                usage.LastOperation.OutputTokens,
                usage.LastOperation.CachedInputTokens,
                usage.LastOperation.CacheReadTokens,
                usage.LastOperation.CacheWriteTokens,
                usage.LastOperation.ReasoningTokens,
                usage.LastOperation.DurationMs,
                usage.LastOperation.Cost,
                usage.LastOperation.Initiator,
                usage.LastOperation.ParentToolCallId,
                usage.LastOperation.ReasoningEffort,
                usage.LastOperation.Label,
            },
            scope = usage.Scope.ToString(),
            source = usage.Source.ToString(),
            usage.UpdatedAt,
        };

    private static object ToProviderOperationTotalsPayload(ProviderOperationTotals totals)
        => new
        {
            totals.OperationCount,
            totals.InputTokens,
            totals.OutputTokens,
            totals.CachedInputTokens,
            totals.ReasoningTokens,
        };

    private static double? ToMilliseconds(TimeSpan? value)
        => value?.TotalMilliseconds;

    private static string MetricsScopeWire(SessionMetricsScope scope)
        => scope == SessionMetricsScope.LastTurn ? "last-turn" : "session";

    private static bool IsPendingQueuedPromptState(string? state)
        => string.Equals(state, "queued", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(state, "submitting", StringComparison.OrdinalIgnoreCase);

    private static void WriteModelSelection(AltaCommandContext context, string type, AltaModelSelection selection, string? sessionId)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            sessionId,
            selection.ProviderKey,
            selection.ModelId,
            reasoningEffort = selection.ReasoningEffort is null ? null : AltaModelRef.ToWireName(selection.ReasoningEffort.Value),
            selection.ModelRef,
        });
    }

    private static object ToModelSelectionPayload(AltaModelSelection selection)
        => new
        {
            selection.ProviderKey,
            selection.ModelId,
            reasoningEffort = selection.ReasoningEffort is null ? null : AltaModelRef.ToWireName(selection.ReasoningEffort.Value),
            selection.ModelRef,
        };

    private static void WriteAgentEvent(AltaCommandContext context, SessionViewDescriptor session, AgentEvent agentEvent, long sequenceNumber, IReadOnlySet<string>? fields = null)
    {
        var mapped = MapAgentEvent(agentEvent);
        if (fields is { Count: > 0 })
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, CreateAgentEventFieldsRecord(context, session, agentEvent, sequenceNumber, mapped, fields));
            return;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.event",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = session.SessionId,
            sequenceNumber,
            timestamp = agentEvent.Timestamp,
            kind = mapped.Kind,
            role = mapped.Role,
            source = new
            {
                // Compatibility: event-source kind is a live-tool wire value from before provider terminology.
                kind = "backend",
                ProviderId = agentEvent.ProviderId.Value,
                runId = agentEvent.RunId?.Value,
            },
            content = string.IsNullOrEmpty(mapped.Text) ? [] : new[] { new { type = "text", text = mapped.Text } },
            metadata = mapped.Metadata,
        });
    }

    private static Dictionary<string, object?> CreateAgentEventFieldsRecord(
        AltaCommandContext context,
        SessionViewDescriptor session,
        AgentEvent agentEvent,
        long sequenceNumber,
        MappedEvent mapped,
        IReadOnlySet<string> fields)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.session.event",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
        };

        if (ContainsField(fields, "sessionId"))
        {
            record["sessionId"] = session.SessionId;
        }

        if (ContainsField(fields, "sequenceNumber") || ContainsField(fields, "sequence"))
        {
            record["sequenceNumber"] = sequenceNumber;
        }

        if (ContainsField(fields, "timestamp"))
        {
            record["timestamp"] = agentEvent.Timestamp;
        }

        if (ContainsField(fields, "kind"))
        {
            record["kind"] = mapped.Kind;
        }

        if (ContainsField(fields, "role"))
        {
            record["role"] = mapped.Role;
        }

        if (ContainsField(fields, "source"))
        {
            record["source"] = new
            {
                // Compatibility: event-source kind is a live-tool wire value from before provider terminology.
                kind = "backend",
                ProviderId = agentEvent.ProviderId.Value,
                runId = agentEvent.RunId?.Value,
            };
        }

        if (ContainsField(fields, "content"))
        {
            record["content"] = string.IsNullOrEmpty(mapped.Text) ? [] : new[] { new { type = "text", text = mapped.Text } };
        }

        if (ContainsField(fields, "text"))
        {
            record["text"] = mapped.Text;
        }

        if (ContainsField(fields, "metadata"))
        {
            record["metadata"] = mapped.Metadata;
        }

        return record;
    }

    private static bool ContainsField(IReadOnlySet<string> fields, string field)
        => fields.Contains(field);

    private static MappedEvent MapAgentEvent(AgentEvent agentEvent)
        => agentEvent switch
        {
            AgentContentDeltaEvent content => new MappedEvent(ContentKindToEventKind(content.Kind), ContentKindToRole(content.Kind), content.Delta, null),
            AgentContentCompletedEvent content => new MappedEvent(ContentKindToEventKind(content.Kind), ContentKindToRole(content.Kind), content.Content, null),
            AgentActivityEvent activity => new MappedEvent($"activity.{activity.Kind.ToString().ToLowerInvariant()}.{activity.Phase.ToString().ToLowerInvariant()}", "host", activity.Message ?? activity.Name, new { activity.ActivityId, activity.ParentActivityId }),
            AgentSessionUpdateEvent update => new MappedEvent($"host.{update.Kind.ToString().ToLowerInvariant()}", "host", update.Message, null),
            AgentPlanSnapshotEvent plan => new MappedEvent("assistant.plan", "assistant", plan.Snapshot.Explanation, new { plan.Snapshot.ChangeKind, steps = plan.Snapshot.Steps }),
            AgentInteractionEvent interaction => new MappedEvent($"interaction.{interaction.Kind.ToString().ToLowerInvariant()}", "host", interaction.Message, new { interaction.InteractionId }),
            AgentErrorEvent error => new MappedEvent("error", "host", error.Message, null),
            AgentRawEvent raw => new MappedEvent("event.raw", "event", raw.BackendEventType, null),
            AgentPermissionRequest permission => new MappedEvent("host.permissionRequest", "host", permission.Kind, new { permission.InteractionId }),
            AgentUserInputRequest request => new MappedEvent("host.userInputRequest", "host", "User input requested.", new { request.InteractionId }),
            _ => new MappedEvent("event", "event", agentEvent.GetType().Name, null),
        };

    private static string ContentKindToEventKind(AgentContentKind kind)
        => kind switch
        {
            AgentContentKind.User => "user.message",
            AgentContentKind.Assistant => "assistant.message",
            AgentContentKind.Reasoning => "assistant.reasoning",
            AgentContentKind.ReasoningSummary => "assistant.reasoningSummary",
            AgentContentKind.Plan => "assistant.plan",
            AgentContentKind.CommandOutput => "tool.commandOutput",
            AgentContentKind.FileChangeOutput => "tool.fileChangeOutput",
            AgentContentKind.ToolOutput => "tool.output",
            AgentContentKind.Notice => "host.notice",
            _ => "event.content",
        };

    private static string ContentKindToRole(AgentContentKind kind)
        => kind switch
        {
            AgentContentKind.User => "user",
            AgentContentKind.Assistant or AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary or AgentContentKind.Plan => "assistant",
            AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput => "tool",
            AgentContentKind.Notice => "host",
            _ => "event",
        };

    private static bool IncludesEvent(AgentEvent agentEvent, IReadOnlySet<string> includes, IReadOnlySet<string> kinds, bool noToolOutput)
    {
        var mapped = MapAgentEvent(agentEvent);
        if (noToolOutput && mapped.Role == "tool")
        {
            return false;
        }

        if (kinds.Count > 0 && !kinds.Contains(mapped.Kind))
        {
            return false;
        }

        if (includes.Count == 0)
        {
            return true;
        }

        return includes.Contains(mapped.Role) ||
               mapped.Kind.Split('.', 2)[0] is { } prefix && includes.Contains(prefix) ||
               (mapped.Role == "host" && includes.Contains("event"));
    }

    private static IReadOnlySet<string> ParseSet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            set.Add(part);
        }

        return set;
    }

    private static void WritePromptResult(AltaCommandContext context, string type, SessionViewDescriptor session, string? runId, string? queueItemId, bool queued, PromptDispatchKind kind, string prompt)
    {
        var parentNotificationExpected = IsAgentCaller(context) && !string.IsNullOrWhiteSpace(session.ParentSessionId);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = session.SessionId,
            runId,
            queueItemId,
            queued,
            detached = runId is null && queueItemId is null && !queued,
            dispatchKind = kind.ToString().ToLowerInvariant(),
            notificationExpected = parentNotificationExpected ? true : (bool?)null,
            shouldPoll = parentNotificationExpected ? false : (bool?)null,
            shouldYield = parentNotificationExpected ? true : (bool?)null,
            activeWaitAllowed = parentNotificationExpected ? false : (bool?)null,
            waitForCompletion = parentNotificationExpected ? false : (bool?)null,
            followUpMode = parentNotificationExpected ? "notification" : null,
            recommendedAction = parentNotificationExpected ? "stop" : null,
            forbiddenWaitActions = parentNotificationExpected ? NotificationFollowUpForbiddenWaitActions : null,
            nextStep = parentNotificationExpected ? NotificationFollowUpNextStep : null,
            notification = CreatePromptNotificationPayload(context, session),
            submittedBy = CreateProvenance(context),
            promptPreview = prompt.Length <= 160 ? prompt : prompt[..160],
        });
    }

    private static void WriteReminder(AltaCommandContext context, string type, AltaReminderDescriptor reminder)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            reminderId = reminder.ReminderId,
            sessionId = reminder.TargetSessionId,
            sourceSessionId = reminder.SourceSessionId,
            state = reminder.State,
            durationSeconds = reminder.Duration.TotalSeconds,
            repeat = reminder.RepeatCount,
            firedCount = reminder.FiredCount,
            createdAt = reminder.CreatedAt,
            dueAt = reminder.DueAt,
            lastFiredAt = reminder.LastFiredAt,
            completedAt = reminder.CompletedAt,
            lastExitCode = reminder.LastExitCode,
            lastError = reminder.LastError,
            contentPreview = reminder.ContentPreview,
            lastTranscriptPreview = reminder.LastTranscriptPreview,
        });
    }

    private static object? CreatePromptNotificationPayload(AltaCommandContext context, SessionViewDescriptor session)
        => IsAgentCaller(context) && !string.IsNullOrWhiteSpace(session.ParentSessionId)
            ? new
            {
                parentSessionId = session.ParentSessionId,
                automaticParentNotification = true,
                expected = true,
                shouldPoll = false,
                shouldYield = true,
                activeWaitAllowed = false,
                waitForCompletion = false,
                followUpMode = "notification",
                recommendedAction = "stop",
                forbiddenWaitActions = NotificationFollowUpForbiddenWaitActions,
                nextStep = NotificationFollowUpNextStep,
                guidance = NotificationFollowUpGuidance,
            }
            : null;

    private static void WriteSkill(AltaCommandContext context, string type, SkillDescriptor skill, bool includeDiagnostics, SkillDocument? document = null)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            name = skill.Name,
            skill.Title,
            skill.Description,
            skillRootPath = skill.SkillRootPath,
            skillFilePath = skill.SkillFilePath,
            sourceKind = skill.SourceKind.ToString().ToLowerInvariant(),
            sourceId = skill.SourceId,
            scope = skill.Scope.ToString().ToLowerInvariant(),
            skill.IsTrusted,
            skill.IsValid,
            skill.IsModelVisible,
            skill.IsShadowed,
            skill.ShadowedBySkillFilePath,
            diagnostics = includeDiagnostics ? skill.Diagnostics.Select(static diagnostic => new { severity = diagnostic.Severity.ToString().ToLowerInvariant(), diagnostic.Code, diagnostic.Message, diagnostic.FieldName, diagnostic.Path }).ToArray() : null,
            body = document?.Body,
        });
    }

    private static void WriteSkillRefs(AltaCommandContext context, IReadOnlyList<SkillDescriptor> skills)
        => AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.skill.refs",
            skills = skills.Select(static skill => skill.Name).ToArray(),
        });

    private static void WritePolicy(AltaCommandContext context, string type, AltaCommandPolicy policy)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            policy.Path,
            policy.RequiresInProcessRuntime,
            policy.IsMutating,
            policy.IsDisruptive,
            policy.SupportsCatalogOnlyContext,
        });
    }

    private static void WriteToolPaths(AltaCommandContext context, IReadOnlyList<AltaCommandPolicy> policies)
    {
        var ordered = policies.OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.tool.paths",
            paths = ordered.Select(static policy => policy.Path).ToArray(),
            mutating = ordered.Where(static policy => policy.IsMutating).Select(static policy => policy.Path).ToArray(),
            disruptive = ordered.Where(static policy => policy.IsDisruptive).Select(static policy => policy.Path).ToArray(),
        });
    }

    private static void WriteToolCapabilities(AltaCommandContext context, IReadOnlyList<AltaCommandPolicy> policies)
    {
        var ordered = policies.OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var runtimeCapabilities = GetRuntimeCapabilities(context);
        var providerCapabilities = GetProviderCapabilities(context);
        var pluginCapability = GetPluginCapability(context, policies);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.tool.capabilities",
            paths = ordered.Select(static policy => policy.Path).ToArray(),
            mutating = ordered.Where(static policy => policy.IsMutating).Select(static policy => policy.Path).ToArray(),
            disruptive = ordered.Where(static policy => policy.IsDisruptive).Select(static policy => policy.Path).ToArray(),
            catalogOnly = ordered.Where(static policy => policy.SupportsCatalogOnlyContext).Select(static policy => policy.Path).ToArray(),
            outOfProcess = ordered.Where(static policy => !policy.RequiresInProcessRuntime).Select(static policy => policy.Path).ToArray(),
            runtime = new
            {
                available = runtimeCapabilities.Where(static capability => capability.Available).Select(static capability => capability.Capability).ToArray(),
                unavailable = runtimeCapabilities.Where(static capability => !capability.Available).Select(static capability => capability.Capability).ToArray(),
            },
            providers = new
            {
                registered = providerCapabilities.Where(static capability => capability.Registered).Select(static capability => capability.ProviderKey).ToArray(),
                configured = providerCapabilities.Where(static capability => capability.Configured).Select(static capability => capability.ProviderKey).ToArray(),
                sessionTool = providerCapabilities.Where(static capability => capability.SupportsAltaSessionTool).Select(static capability => capability.ProviderKey).ToArray(),
            },
            // Compatibility: keep the legacy aggregate field for existing live-tool clients.
            backends = new
            {
                registered = providerCapabilities.Where(static capability => capability.Registered).Select(static capability => capability.ProviderKey).ToArray(),
                configured = providerCapabilities.Where(static capability => capability.Configured).Select(static capability => capability.ProviderKey).ToArray(),
                sessionTool = providerCapabilities.Where(static capability => capability.SupportsAltaSessionTool).Select(static capability => capability.ProviderKey).ToArray(),
            },
            plugins = pluginCapability,
        });
    }

    private static void WriteModelItem(AltaCommandContext context, string providerKey, AgentModelInfo model, AgentReasoningEffort? requestedReasoning = null)
    {
        var reasoning = requestedReasoning ?? model.DefaultReasoningEffort;
        var reasoningStatus = GetReasoningStatus(model, requestedReasoning, reasoning);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.model.item",
            version = 1,
            correlationId = context.CorrelationId,
            providerKey,
            modelId = model.Id,
            model.DisplayName,
            model.Description,
            model.Provider,
            defaultReasoningEffort = model.DefaultReasoningEffort is null ? null : AltaModelRef.ToWireName(model.DefaultReasoningEffort.Value),
            supportedReasoningEfforts = model.SupportedReasoningEfforts?.Select(AltaModelRef.ToWireName).ToArray(),
            requestedReasoningEffort = requestedReasoning is null ? null : AltaModelRef.ToWireName(requestedReasoning.Value),
            effectiveReasoningEffort = reasoning is null ? null : AltaModelRef.ToWireName(reasoning.Value),
            reasoningStatus,
            modelRef = AltaModelRef.Format(providerKey, model.Id, reasoning),
            capabilities = model.Capabilities,
        });
    }

    private static void WriteModelRefs(AltaCommandContext context, IReadOnlyList<string> modelRefs)
        => AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.model.refs",
            modelRefs,
        });

    private static void WriteProviderKeys(AltaCommandContext context, IReadOnlyList<ModelProviderId> ProviderIds)
        => AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.provider.keys",
            providerKeys = ProviderIds.Select(static ProviderId => ProviderId.Value).ToArray(),
        });

    private static string CreateModelRef(string providerKey, AgentModelInfo model, AgentReasoningEffort? requestedReasoning)
    {
        var reasoning = requestedReasoning ?? model.DefaultReasoningEffort;
        return AltaModelRef.Format(providerKey, model.Id, reasoning);
    }

    private static bool ModelMatchesFilters(string providerKey, AgentModelInfo model, ModelListOptions options)
    {
        if (options.ReasoningEffort is { } reasoning && !ModelSupportsReasoning(model, reasoning))
        {
            return false;
        }

        if (options.SupportsTools && !ModelSupportsTools(model))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.Contains))
        {
            var effectiveReasoning = options.ReasoningEffort ?? model.DefaultReasoningEffort;
            var modelRef = AltaModelRef.Format(providerKey, model.Id, effectiveReasoning);
            var needle = options.Contains.Trim();
            return ContainsOrdinalIgnoreCase(model.Id, needle) ||
                   ContainsOrdinalIgnoreCase(model.DisplayName, needle) ||
                   ContainsOrdinalIgnoreCase(modelRef, needle);
        }

        return true;
    }

    private static bool ModelSupportsReasoning(AgentModelInfo model, AgentReasoningEffort reasoning)
    {
        if (reasoning == AgentReasoningEffort.None)
        {
            return true;
        }

        return model.SupportedReasoningEfforts is { Count: > 0 }
            ? model.SupportedReasoningEfforts.Contains(reasoning)
            : ModelCapabilityBool(model, "supportsReasoning") != false;
    }

    private static bool ModelSupportsTools(AgentModelInfo model)
        => ModelCapabilityBool(model, "supportsToolCall") == true || ModelCapabilityBool(model, "supportsTools") == true;

    private static bool? ModelCapabilityBool(AgentModelInfo model, string key)
    {
        if (model.Capabilities is null || !model.Capabilities.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            bool boolValue => boolValue,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string GetReasoningStatus(AgentModelInfo model, AgentReasoningEffort? requestedReasoning, AgentReasoningEffort? effectiveReasoning)
    {
        if (requestedReasoning is { } requested)
        {
            return ModelSupportsReasoning(model, requested) ? "applied" : "unsupported";
        }

        return effectiveReasoning is null ? "none" : "defaulted";
    }

    private static bool ContainsOrdinalIgnoreCase(string? value, string text)
        => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private static void WritePlugin(AltaCommandContext context, string type, AltaPluginSummary plugin)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            plugin.RuntimeKey,
            plugin.DisplayName,
            pluginVersion = plugin.Version,
            plugin.Scope,
            plugin.State,
            plugin.Diagnostics,
        });
    }

    private static void WritePluginRefs(AltaCommandContext context, IReadOnlyList<AltaPluginSummary> plugins)
        => AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.plugin.refs",
            plugins = plugins.Select(static plugin => new
            {
                runtimeKey = plugin.RuntimeKey,
                state = plugin.State,
            }).ToArray(),
        });

    private static void WriteSummary(AltaCommandContext context, string type, int count, bool truncated)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            count,
            truncated,
        });
    }

    private static int UsageError(AltaCommandContext context, string code, string message, string commandPath)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.Usage, message, commandPath, $"Use `{commandPath} --help` for usage.");
        return AltaExitCodes.Usage;
    }

    private static int NotFound(AltaCommandContext context, string code, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.NotFound, message);
        return AltaExitCodes.NotFound;
    }

    private static int PermissionDenied(AltaCommandContext context, string code, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.PolicyDenied, message);
        return AltaExitCodes.PolicyDenied;
    }

    private static int Unsupported(AltaCommandContext context, string code, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.Unsupported, message);
        return AltaExitCodes.Unsupported;
    }

    private static AltaActorProvenance CreateProvenance(AltaCommandContext context)
        => new()
        {
            Kind = context.Caller.Kind,
            SourceSessionId = context.Caller.SourceSessionId,
            SourceProjectId = context.Caller.SourceProjectId,
            SourceAgentId = context.Caller.SourceAgentId,
            PluginRuntimeKey = context.Caller.PluginRuntimeKey,
            CorrelationId = context.CorrelationId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string SessionKindWire(SessionViewKind kind)
        => kind switch
        {
            SessionViewKind.GlobalSession => "global_session",
            SessionViewKind.ProjectSession => "project_session",
            SessionViewKind.InternalSession => "internal_session",
            _ => kind.ToString().ToLowerInvariant(),
        };

    private static string SessionStatusWire(SessionViewStatus status)
        => status.ToString().ToLowerInvariant();

    private sealed class AltaModelSelectionOptions
    {
        public string? ModelRef { get; set; }

        public string? SameModelAsSessionId { get; set; }

        public string? ProviderKey { get; set; }

        public string? ModelId { get; set; }

        public AgentReasoningEffort? ReasoningEffort { get; set; }
    }

    private sealed class SessionCreateOptions
    {
        public AltaModelSelectionOptions Model { get; } = new();

        public string? Project { get; set; }

        public bool Global { get; set; }

        public string? Title { get; set; }

        public string? ParentSessionId { get; set; }

        public bool NoParent { get; set; }
    }

    private sealed class SessionEventsOptions
    {
        public string? Include { get; set; }

        public string? Kind { get; set; }

        public string? Fields { get; set; }

        public bool NoToolOutput { get; set; }
    }

    private sealed class ModelListOptions
    {
        public string? Provider { get; set; }

        public string? Contains { get; set; }

        public AgentReasoningEffort? ReasoningEffort { get; set; }

        public bool SupportsTools { get; set; }

        public bool Detailed { get; set; }
    }

    private enum SessionMetricsScope
    {
        LastTurn,
        Session,
    }

    private sealed record ProviderOperationTotals(
        int OperationCount,
        long InputTokens,
        long OutputTokens,
        long CachedInputTokens,
        long ReasoningTokens);

    private sealed record ProviderCapability(
        string ProviderKey,
        string ProviderId,
        bool Registered,
        bool Configured,
        bool SupportsAltaSessionTool);

    private sealed record PluginCapability(
        bool Available,
        int PluginCount,
        int PluginCommandCount);

    private sealed record SessionMetrics(
        SessionMetricsScope Scope,
        int EventCount,
        int RunCount,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        TimeSpan? Duration,
        DateTimeOffset? FinalAssistantAt,
        int UserMessageCount,
        int AssistantMessageCount,
        int ToolCallCount,
        int FinalAnswerCharacters,
        int FinalAnswerWords,
        long FinalAnswerEstimatedTokens,
        AgentSessionUsage? FinalUsage,
        ProviderOperationTotals ProviderOperations,
         string Status,
         FinalErrorPayload? FinalError,
        object ModelSelection);

    private sealed record SessionResult(
        SessionMetricsScope Scope,
        SessionResultStatus Status,
        string? FinalAnswer,
        DateTimeOffset? FinalAssistantAt,
        FinalErrorPayload? FinalError,
        SessionMetrics Metrics);

    private sealed record SessionReportItem(
        int Index,
        string SessionId,
        AltaSessionInfo? Info,
        SessionResult? Result,
        string Diagnostics)
    {
        public static SessionReportItem Success(int index, string sessionId, AltaSessionInfo info, SessionResult result, string diagnostics)
            => new(index, sessionId, info, result, diagnostics);

        public static SessionReportItem NotFound(int index, string sessionId)
            => new(index, sessionId, null, null, string.Empty);
    }

    private sealed record FinalErrorPayload(
        string kind,
        string message,
        DateTimeOffset timestamp,
        string? runId,
        string? exceptionType);

    private enum SessionResultStatus
    {
        Unknown,
        Running,
        Completed,
        Failed,
        Cancelled,
    }

    private sealed class PromptOptions
    {
        public string? Message { get; set; }

        public bool UseStdin { get; set; }

        public string? PromptId { get; set; }

        public bool QueueIfBusy { get; set; }

        public string? MessageKind { get; set; }

        public bool ReplyRequested { get; set; }
    }

    private sealed class ReminderCreateOptions
    {
        public string? Duration { get; set; }

        public string? Content { get; set; }

        public bool UseStdin { get; set; }

        public string? SessionId { get; set; }

        public string? Repeat { get; set; }
    }

    private sealed class PromptManagementOptions
    {
        public string? Scope { get; set; } = "all";

        public string? Project { get; set; }

        public bool System { get; set; }

        public bool Verbose { get; set; }

        public string? Content { get; set; }

        public bool UseStdin { get; set; }

        public string? Name { get; set; }

        public string? Description { get; set; }

        public string? SystemPromptId { get; set; }
    }

    private sealed record PromptMetadata(string? Name, string? Description)
    {
        public static PromptMetadata Empty { get; } = new(null, null);
    }

    private sealed record MappedEvent(string Kind, string Role, string? Text, object? Metadata);

    private sealed record ModelResolutionResult(int ExitCode, AltaModelSelection? Selection)
    {
        public static ModelResolutionResult Success(AltaModelSelection selection) => new(AltaExitCodes.Success, selection);

        public static ModelResolutionResult Fail(int exitCode) => new(exitCode, null);
    }

    private sealed record SessionResultBuildResult(int ExitCode, AltaSessionInfo? Info, SessionResult? Result)
    {
        public static SessionResultBuildResult Success(AltaSessionInfo info, SessionResult result) => new(AltaExitCodes.Success, info, result);

        public static SessionResultBuildResult Fail(int exitCode) => new(exitCode, null, null);
    }

    private sealed record SessionInfoResolutionResult(int ExitCode, AltaSessionInfo? Info, IReadOnlyList<AltaSessionInfo> Infos)
    {
        public static SessionInfoResolutionResult Success(AltaSessionInfo info, IReadOnlyList<AltaSessionInfo> infos) => new(AltaExitCodes.Success, info, infos);

        public static SessionInfoResolutionResult Fail(int exitCode) => new(exitCode, null, []);
    }

    private sealed record SessionModelSelectionResult(int ExitCode, bool Found, AltaModelSelection? Selection)
    {
        public static SessionModelSelectionResult Success(AltaModelSelection selection) => new(AltaExitCodes.Success, Found: true, selection);

        public static SessionModelSelectionResult NotFound() => new(AltaExitCodes.Success, Found: false, null);

        public static SessionModelSelectionResult Fail(int exitCode) => new(exitCode, Found: false, null);
    }

    private sealed record SkillQueryResult(int ExitCode, SkillCatalogQuery? Query)
    {
        public static SkillQueryResult Success(SkillCatalogQuery query) => new(AltaExitCodes.Success, query);

        public static SkillQueryResult Fail(int exitCode) => new(exitCode, null);
    }

    private sealed record PromptQueryResult(int ExitCode, UserPromptCatalogQuery? Query)
    {
        public static PromptQueryResult Success(UserPromptCatalogQuery query) => new(AltaExitCodes.Success, query);

        public static PromptQueryResult Fail(int exitCode) => new(exitCode, null);
    }

    private sealed record PromptProjectRootResult(int ExitCode, string? ProjectRoot)
    {
        public static PromptProjectRootResult Success(string? projectRoot) => new(AltaExitCodes.Success, projectRoot);

        public static PromptProjectRootResult Fail(int exitCode) => new(exitCode, null);
    }

    private sealed record PromptReadResult(int ExitCode, string? Prompt)
    {
        public static PromptReadResult Success(string prompt) => new(AltaExitCodes.Success, prompt);

        public static PromptReadResult Fail(int exitCode) => new(exitCode, null);
    }

    private sealed record AltaPluginAgentRunAugmentation(SessionExecutionOptions ExecutionOptions, AgentInput Input, string? CancelReason = null);

    private sealed record ParentSessionResolutionResult(int ExitCode, string? ParentSessionId)
    {
        public static ParentSessionResolutionResult Success(string? parentSessionId) => new(AltaExitCodes.Success, parentSessionId);

        public static ParentSessionResolutionResult Fail(int exitCode) => new(exitCode, null);
    }

    private enum PromptDispatchKind
    {
        Send,
        Steer,
        Queue,
        Message,
        Request,
    }
}
