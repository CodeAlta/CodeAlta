using System.Reflection;
using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Skills;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

internal sealed class BuiltInAltaCommandContributor : IAltaCommandContributor
{
    private static readonly AltaCommandPolicy[] Policies =
    [
        Read("version", supportsCatalogOnlyContext: true),
        Read("project list", supportsCatalogOnlyContext: true),
        Read("project show", supportsCatalogOnlyContext: true),
        Read("project resolve", supportsCatalogOnlyContext: true),
        Mutating("project upsert", requiresRuntime: false, supportsCatalogOnlyContext: true),
        Read("session list"),
        Read("session show"),
        Read("session status"),
        Read("session children"),
        Read("session model"),
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
        Read("plugin list"),
        Read("plugin status"),
    ];

    public IEnumerable<CommandNode> CreateCommandLineNodes(AltaCommandContributionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        yield return CreateVersionCommand(context.Invocation);
        yield return CreateProjectCommand(context.Invocation);
        yield return CreateSessionCommand(context.Invocation);
        yield return CreateSkillCommand(context.Invocation);
        yield return CreateSkillsAliasCommand(context.Invocation);
        yield return CreateSkillsActivateAliasCommand(context.Invocation);
        yield return CreateToolCommand(context.Invocation);
        yield return CreateProviderCommand(context.Invocation);
        yield return CreateModelCommand(context.Invocation);
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
        group.Add(CreateProjectUpsertCommand(context));
        return group;
    }

    private static Command CreateProjectListCommand(AltaCommandContext context)
    {
        var includeArchived = false;
        var command = Leaf("list", "List known projects from the CodeAlta catalog.");
        command.Add("include-archived", "Include archived projects.", value => includeArchived = value is not null);
        command.Add(async (_, _) => await HandleProjectListAsync(context, includeArchived).ConfigureAwait(false));
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
        group.Add(CreateSessionChildrenCommand(context));
        group.Add(CreateSessionModelCommand(context));
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
        return group;
    }

    private static Command CreateSessionListCommand(AltaCommandContext context)
    {
        string? project = null;
        string? state = null;
        string? backend = null;
        var limit = 50;
        var command = Leaf("list", "List recoverable/live sessions as JSONL.");
        command.Add("project=", "Filter by project id, slug, or path.", value => project = value);
        command.Add("state=", "Filter by running, idle, inactive, archived, or all.", value => state = ValidateState(value));
        command.Add("backend=", "Filter by backend/provider id.", value => backend = value);
        command.Add("limit=", "Maximum sessions to return.", (int value) => limit = value);
        command.Add(async (_, _) => await HandleSessionListAsync(context, project, state, backend, limit).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionShowCommand(AltaCommandContext context)
    {
        string? threadId = null;
        var command = Leaf("show", "Show one session descriptor and live status.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        command.Add(async (_, _) => await HandleSessionShowAsync(context, threadId, "alta.session.detail").ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionStatusCommand(AltaCommandContext context)
    {
        string? threadId = null;
        var command = Leaf("status", "Show compact live status for one session.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        command.Add(async (_, _) => await HandleSessionShowAsync(context, threadId, "alta.session.status").ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionChildrenCommand(AltaCommandContext context)
    {
        string? threadId = null;
        var recursive = false;
        var command = Leaf("children", "List child sessions for a parent thread.");
        command.Add("<thread-id>", "Parent thread id.", value => threadId = value);
        command.Add("recursive", "Include descendants recursively.", value => recursive = value is not null);
        command.Add(async (_, _) => await HandleSessionChildrenAsync(context, threadId, recursive).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionModelCommand(AltaCommandContext context)
    {
        string? threadId = null;
        var command = Leaf("model", "Show the provider/model/reasoning selection for one session.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        command.Add(async (_, _) => await HandleSessionModelAsync(context, threadId).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionTailCommand(AltaCommandContext context)
    {
        string? threadId = null;
        var last = 20;
        string? include = null;
        var command = Leaf("tail", "Return a finite sanitized snapshot of recent session events.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        command.Add("last=", "Number of recent events to return.", (int value) => last = value);
        command.Add("include=", "Comma-separated categories: user,assistant,reasoning,tool,host,event.", value => include = value);
        command.Add(async (_, _) => await HandleSessionEventsAsync(context, threadId, since: null, limit: last, include, fromTail: true).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionEventsCommand(AltaCommandContext context)
    {
        string? threadId = null;
        long? since = null;
        var limit = 50;
        string? include = null;
        var command = Leaf("events", "Return a finite sanitized snapshot of session events.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        command.Add("since=", "Return events after this sequence number.", (long value) => since = value);
        command.Add("limit=", "Maximum events to return.", (int value) => limit = value);
        command.Add("include=", "Comma-separated categories: user,assistant,reasoning,tool,host,event.", value => include = value);
        command.Add(async (_, _) => await HandleSessionEventsAsync(context, threadId, since, limit, include, fromTail: false).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionCreateCommand(AltaCommandContext context)
    {
        var options = new SessionCreateOptions();
        var command = Leaf("create", "Create a project or global session using resolved model selection.");
        AddModelSelectionOptions(command, options.Model);
        command.Add("project=", "Target project id, slug, or path.", value => options.Project = value);
        command.Add("global", "Create a global coordinator session.", value => options.Global = value is not null);
        command.Add("title=", "Session title.", value => options.Title = value);
        command.Add("parent=", "Explicit parent thread id for same-project lineage.", value => options.ParentThreadId = value);
        command.Add("no-parent", "Suppress automatic parent assignment.", value => options.NoParent = value is not null);
        command.Add(async (_, _) => await HandleSessionCreateAsync(context, options).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionSendCommand(AltaCommandContext context)
    {
        string? threadId = null;
        var options = new PromptOptions();
        var command = Leaf("send", "Submit a normal prompt to a session and return run metadata.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        AddMessageOptions(command, options);
        command.Add("queue-if-busy", "Queue instead of failing when the target is busy, if a queue service is available.", value => options.QueueIfBusy = value is not null);
        command.Add(async (_, _) => await HandleSessionSendAsync(context, threadId, options, PromptDispatchKind.Send).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionSteerCommand(AltaCommandContext context)
    {
        string? threadId = null;
        var options = new PromptOptions();
        var command = Leaf("steer", "Send steering input to an active run.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        AddMessageOptions(command, options);
        command.Add(async (_, _) => await HandleSessionSendAsync(context, threadId, options, PromptDispatchKind.Steer).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionQueueCommand(AltaCommandContext context)
    {
        string? threadId = null;
        var options = new PromptOptions();
        var command = Leaf("queue", "Queue a prompt for later session submission when a queue service is available.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        AddMessageOptions(command, options);
        command.Add(async (_, _) => await HandleSessionSendAsync(context, threadId, options, PromptDispatchKind.Queue).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionAbortCommand(AltaCommandContext context)
    {
        string? threadId = null;
        string? reason = null;
        var command = Leaf("abort", "Abort active work in a session.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        command.Add("reason=", "Abort reason for diagnostics.", value => reason = value);
        command.Add(async (_, _) => await HandleSessionAbortAsync(context, threadId, reason).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionCompactCommand(AltaCommandContext context)
    {
        string? threadId = null;
        var submit = false;
        var command = Leaf("compact", "Request manual compaction for a session.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        command.Add("submit", "Reserved for submit-after-compaction behavior.", value => submit = value is not null);
        command.Add(async (_, _) => await HandleSessionCompactAsync(context, threadId, submit).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionJoinCommand(AltaCommandContext context)
    {
        string? threadId = null;
        var command = Leaf("join", "Return non-blocking context needed to address a target session later.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        command.Add(async (_, _) => await HandleSessionShowAsync(context, threadId, "alta.session.join").ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionMessageCommand(AltaCommandContext context)
    {
        string? threadId = null;
        var options = new PromptOptions { MessageKind = "note" };
        var command = Leaf("message", "Send an attributed peer-agent message to a session.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        AddMessageOptions(command, options);
        command.Add("kind=", "Message kind: note, request, handoff, or answer.", value => options.MessageKind = ValidateMessageKind(value));
        command.Add(async (_, _) => await HandleSessionSendAsync(context, threadId, options, PromptDispatchKind.Message).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSessionRequestCommand(AltaCommandContext context)
    {
        string? threadId = null;
        var options = new PromptOptions { MessageKind = "request" };
        var command = Leaf("request", "Send an attributed peer-agent request to a session.");
        command.Add("<thread-id>", "CodeAlta thread id.", value => threadId = value);
        AddMessageOptions(command, options);
        command.Add("reply-requested", "Annotate the request as expecting a reply.", value => options.ReplyRequested = value is not null);
        command.Add(async (_, _) => await HandleSessionSendAsync(context, threadId, options, PromptDispatchKind.Request).ConfigureAwait(false));
        return command;
    }

    private static Command CreateSkillCommand(AltaCommandContext context)
    {
        var group = Group("skill", "Discover and activate CodeAlta-managed skills.");
        group.Add(CreateSkillListCommand(context));
        group.Add(CreateSkillShowCommand(context));
        group.Add(CreateSkillActivateCommand(context, "activate"));
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
        var command = Leaf("list", "List discovered CodeAlta skills.");
        command.Add("project=", "Project id, slug, or path for project-local skill roots.", value => project = value);
        command.Add(async (_, _) => await HandleSkillListAsync(context, project).ConfigureAwait(false));
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
        string? threadId = null;
        var command = Leaf(name, name == "skills_activate" ? "Compatibility skill activation command." : "Activate a CodeAlta-managed skill for a session.");
        command.Add("<skill-name>", "Skill name.", value => skillName = value);
        command.Add("session=", "Target session/thread id.", value => threadId = value);
        command.Add(async (_, _) => await HandleSkillActivateAsync(context, skillName, threadId).ConfigureAwait(false));
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
        var command = Leaf("list", "List built-in alta command policy entries.");
        command.Add((_, _) =>
        {
            var policies = GetEffectivePolicies(context);
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
        var command = Leaf("list", "List command capability classifications.");
        command.Add((_, _) =>
        {
            var policies = GetEffectivePolicies(context);
            foreach (var policy in policies.OrderBy(static policy => policy.Path, StringComparer.OrdinalIgnoreCase))
            {
                WritePolicy(context, "alta.tool.capability", policy);
            }

            WriteSummary(context, "alta.tool.capability.summary", policies.Count, truncated: false);
            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static Command CreateProviderCommand(AltaCommandContext context)
    {
        var group = Group("provider", "Inspect registered providers/backends.");
        var model = Group("model", "Inspect provider models.");
        model.Add(CreateModelListCommand(context, providerGroupAlias: true));
        group.Add(CreateProviderListCommand(context));
        group.Add(model);
        return group;
    }

    private static Command CreateProviderListCommand(AltaCommandContext context)
    {
        var command = Leaf("list", "List registered provider/backend ids.");
        command.Add(async (_, _) => await HandleProviderListAsync(context).ConfigureAwait(false));
        return command;
    }

    private static Command CreateModelCommand(AltaCommandContext context)
    {
        var group = Group("model", "Inspect and resolve provider model selections.");
        group.Add(CreateModelListCommand(context, providerGroupAlias: false));
        group.Add(CreateModelShowCommand(context));
        group.Add(CreateModelResolveCommand(context));
        return group;
    }

    private static Command CreateModelListCommand(AltaCommandContext context, bool providerGroupAlias)
    {
        string? provider = null;
        var command = Leaf("list", providerGroupAlias ? "List models for registered providers." : "List models for registered providers.");
        command.Add("provider=", "Provider/backend id filter.", value => provider = value);
        command.Add(async (_, _) => await HandleModelListAsync(context, provider).ConfigureAwait(false));
        return command;
    }

    private static Command CreateModelShowCommand(AltaCommandContext context)
    {
        string? modelRef = null;
        var command = Leaf("show", "Show one model by compact model ref.");
        command.Add("<model-ref>", "Model ref: provider:model[@reasoning].", value => modelRef = value);
        command.Add(async (_, _) => await HandleModelShowAsync(context, modelRef).ConfigureAwait(false));
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

    private static Command CreatePluginCommand(AltaCommandContext context)
    {
        var group = Group("plugin", "Inspect loaded/discovered plugins.");
        group.Add(CreatePluginListCommand(context));
        group.Add(CreatePluginStatusCommand(context));
        return group;
    }

    private static Command CreatePluginListCommand(AltaCommandContext context)
    {
        var command = Leaf("list", "List plugin runtime summaries.");
        command.Add((_, _) => HandlePluginList(context));
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

    private static void AddMessageOptions(Command command, PromptOptions options)
    {
        command.Add("message=", "Prompt/message text.", value => options.Message = value);
        command.Add("stdin", "Read prompt/message text from stdin.", value => options.UseStdin = value is not null);
    }

    private static void AddModelSelectionOptions(Command command, AltaModelSelectionOptions options)
    {
        command.Add("model-ref=", "Compact model ref provider:model[@reasoning].", value => options.ModelRef = value);
        command.Add("same-model-as=", "Inherit model selection from a thread id.", value => options.SameModelAsThreadId = value);
        command.Add("provider=", "Provider/backend id.", value => options.ProviderKey = value);
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

    private static string? ValidateMessageKind(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "note" or "request" or "handoff" or "answer"
            ? normalized
            : throw new CommandOptionException("Message kind must be note, request, handoff, or answer.", "--kind");
    }

    private static async ValueTask<int> HandleProjectListAsync(AltaCommandContext context, bool includeArchived)
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
        string? backendFilter,
        int limit)
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
            .Where(info => CanAccessThread(context, info.Thread))
            .Where(info => project is null || string.Equals(info.Thread.ProjectRef, project.Id, StringComparison.OrdinalIgnoreCase))
            .Where(info => string.IsNullOrWhiteSpace(backendFilter) || string.Equals(info.Thread.BackendId, backendFilter, StringComparison.OrdinalIgnoreCase))
            .Where(info => includeArchived || !string.Equals(info.State, "archived", StringComparison.OrdinalIgnoreCase))
            .Where(info => string.IsNullOrWhiteSpace(stateFilter) || string.Equals(stateFilter, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(info.State, stateFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static info => info.Thread.LastActiveAt)
            .ThenBy(static info => info.Thread.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit + 1)
            .ToArray();

        var truncated = filtered.Length > limit;
        var emitted = truncated ? filtered.Take(limit).ToArray() : filtered;
        foreach (var info in emitted)
        {
            WriteSession(context, "alta.session.item", info, includeChildren: false, infos);
        }

        WriteSummary(context, "alta.session.summary", emitted.Length, truncated);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionShowAsync(AltaCommandContext context, string? threadId, string recordType)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return UsageError(context, "usage.missingThread", "Thread id is required.", "alta session show");
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = FindThread(infos, threadId);
        if (info is null)
        {
            return NotFound(context, "session.notFound", $"Session '{threadId}' was not found.");
        }

        if (!CanAccessThread(context, info.Thread))
        {
            return PermissionDenied(context, "policy.visibilityDenied", $"The caller is not allowed to inspect session '{threadId}'.");
        }

        WriteSession(context, recordType, info, includeChildren: true, infos);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionChildrenAsync(AltaCommandContext context, string? threadId, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return UsageError(context, "usage.missingThread", "Thread id is required.", "alta session children");
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var parent = FindThread(infos, threadId);
        if (parent is null)
        {
            return NotFound(context, "session.notFound", $"Session '{threadId}' was not found.");
        }

        if (!CanAccessThread(context, parent.Thread))
        {
            return PermissionDenied(context, "policy.visibilityDenied", $"The caller is not allowed to inspect session '{threadId}'.");
        }

        var children = GetChildren(infos, parent, recursive)
            .Where(child => CanAccessThread(context, child.Thread))
            .ToArray();
        foreach (var child in children)
        {
            WriteSession(context, "alta.session.item", child, includeChildren: false, infos);
        }

        WriteSummary(context, "alta.session.children.summary", children.Length, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionModelAsync(AltaCommandContext context, string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return UsageError(context, "usage.missingThread", "Thread id is required.", "alta session model");
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = FindThread(infos, threadId);
        if (info is null)
        {
            return NotFound(context, "session.notFound", $"Session '{threadId}' was not found.");
        }

        if (!CanAccessThread(context, info.Thread))
        {
            return PermissionDenied(context, "policy.visibilityDenied", $"The caller is not allowed to inspect session '{threadId}'.");
        }

        WriteModelSelection(context, "alta.model.selection", CreateModelSelection(info.Thread, info.Preference), info.Thread.ThreadId);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionEventsAsync(
        AltaCommandContext context,
        string? threadId,
        long? since,
        int limit,
        string? include,
        bool fromTail)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return UsageError(context, "usage.missingThread", "Thread id is required.", fromTail ? "alta session tail" : "alta session events");
        }

        if (limit <= 0)
        {
            return UsageError(context, "usage.invalidLimit", fromTail ? "--last must be positive." : "--limit must be positive.", fromTail ? "alta session tail" : "alta session events");
        }

        if (!context.TryGetRequired<WorkThreadRuntimeService>(nameof(WorkThreadRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = FindThread(infos, threadId);
        if (info is null)
        {
            return NotFound(context, "session.notFound", $"Session '{threadId}' was not found.");
        }

        if (!CanAccessThread(context, info.Thread))
        {
            return PermissionDenied(context, "policy.visibilityDenied", $"The caller is not allowed to inspect session '{threadId}'.");
        }

        var storedHistoryUnavailable = false;
        var history = await runtime.TryReadStoredHistoryAsync(
                info.Thread,
                _ => storedHistoryUnavailable = true,
                context.CancellationToken)
            .ConfigureAwait(false);
        if (storedHistoryUnavailable)
        {
            AltaJsonlWriter.WriteWarning(
                context.Stderr,
                context.CorrelationId,
                "session.historyStoreUnavailable",
                "Stored session history could not be read; backend history fallback will be used when available.");
        }

        if (history is null || history.Count == 0)
        {
            try
            {
                var activeHistory = await runtime.GetHistoryAsync(info.Thread.ThreadId, context.CancellationToken).ConfigureAwait(false);
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

        if (history is null)
        {
            WriteSummary(context, fromTail ? "alta.session.tail.summary" : "alta.session.events.summary", 0, truncated: false);
            return AltaExitCodes.Success;
        }

        var includes = ParseIncludes(include);
        var records = history
            .Select((agentEvent, index) => (Event: agentEvent, Sequence: (long)index + 1))
            .Where(item => since is null || item.Sequence > since.Value)
            .Where(item => IncludesEvent(item.Event, includes))
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
            WriteAgentEvent(context, info.Thread, item.Event, item.Sequence);
        }

        WriteSummary(context, fromTail ? "alta.session.tail.summary" : "alta.session.events.summary", emitted.Length, truncated);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionCreateAsync(AltaCommandContext context, SessionCreateOptions options)
    {
        if (!context.TryGetRequired<WorkThreadRuntimeService>(nameof(WorkThreadRuntimeService), out var runtime) ||
            !context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (options.Global && !string.IsNullOrWhiteSpace(options.Project))
        {
            return UsageError(context, "usage.projectAndGlobal", "Use either --project or --global, not both.", "alta session create");
        }

        if (!options.Global && string.IsNullOrWhiteSpace(options.Project))
        {
            return UsageError(context, "usage.missingScope", "Session create requires --project <ref> or --global.", "alta session create");
        }

        if (!string.IsNullOrWhiteSpace(options.ParentThreadId) && options.NoParent)
        {
            return UsageError(context, "usage.parentConflict", "Use either --parent or --no-parent, not both.", "alta session create");
        }

        ProjectDescriptor? project = null;
        if (!options.Global)
        {
            project = await ResolveProjectAsync(catalog, options.Project, context, includeArchived: false).ConfigureAwait(false);
            if (project is null)
            {
                return NotFound(context, "project.notFound", $"Project '{options.Project}' was not found.");
            }

            if (!CanAccessProject(context, project.Id))
            {
                return PermissionDenied(context, "policy.visibilityDenied", $"The caller is not allowed to create sessions in project '{project.Id}'.");
            }
        }

        var modelSelection = await ResolveModelSelectionAsync(context, options.Model).ConfigureAwait(false);
        if (modelSelection.ExitCode != AltaExitCodes.Success)
        {
            return modelSelection.ExitCode;
        }

        var workingDirectory = project?.ProjectPath ?? GetGlobalRootOrCwd(context);
        string? createdThreadId = null;
        var executionOptions = BuildExecutionOptions(
            context,
            modelSelection.Selection!,
            workingDirectory,
            project is null ? [] : [project.ProjectPath],
            () => createdThreadId,
            project?.Id);

        WorkThreadDescriptor thread;
        if (project is null)
        {
            thread = await runtime.CreateGlobalThreadAsync(executionOptions, options.Title, context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            thread = await runtime.CreateProjectThreadAsync(project, executionOptions, options.Title, context.CancellationToken).ConfigureAwait(false);
        }

        createdThreadId = thread.ThreadId;
        thread.ParentThreadId = ResolveParentThreadId(context, thread, project, options);
        thread.CreatedBy = CreateProvenance(context);
        await runtime.PersistThreadLocalStateAsync(thread, context.CancellationToken).ConfigureAwait(false);
        await PersistThreadPreferenceAsync(context, thread.ThreadId, modelSelection.Selection!).ConfigureAwait(false);

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.created",
            version = 1,
            correlationId = context.CorrelationId,
            threadId = thread.ThreadId,
            backendId = thread.BackendId,
            providerKey = thread.ResolvedProviderKey,
            backendSessionId = thread.BackendSessionId,
            projectId = thread.ProjectRef,
            title = thread.Title,
            parentThreadId = thread.ParentThreadId,
            createdBy = thread.CreatedBy,
            modelSelection = ToModelSelectionPayload(modelSelection.Selection!),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionSendAsync(
        AltaCommandContext context,
        string? threadId,
        PromptOptions options,
        PromptDispatchKind kind)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return UsageError(context, "usage.missingThread", "Thread id is required.", "alta session send");
        }

        var promptResult = await ReadPromptAsync(context, options, kind).ConfigureAwait(false);
        if (promptResult.ExitCode != AltaExitCodes.Success)
        {
            return promptResult.ExitCode;
        }

        if (!context.TryGetRequired<WorkThreadRuntimeService>(nameof(WorkThreadRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = FindThread(infos, threadId);
        if (info is null)
        {
            return NotFound(context, "session.notFound", $"Session '{threadId}' was not found.");
        }

        if (!CanAccessThread(context, info.Thread))
        {
            return PermissionDenied(context, "policy.visibilityDenied", $"The caller is not allowed to mutate session '{threadId}'.");
        }

        if (kind == PromptDispatchKind.Queue)
        {
            return Unsupported(context, "session.queueUnsupported", "Headless prompt queueing is unavailable in the current runtime context.");
        }

        var inputText = kind is PromptDispatchKind.Message or PromptDispatchKind.Request
            ? BuildPeerAgentMessage(context, info.Thread, options, promptResult.Prompt!)
            : promptResult.Prompt!;
        var executionOptions = await BuildExecutionOptionsForThreadAsync(context, info).ConfigureAwait(false);

        try
        {
            if (kind == PromptDispatchKind.Steer)
            {
                var runId = await runtime.SteerAsync(
                    info.Thread,
                    executionOptions,
                    new AgentSteerOptions { Input = AgentInput.Text(inputText) },
                    context.CancellationToken).ConfigureAwait(false);
                await PersistPromptProvenanceAsync(context, info.Thread, runId.Value, queued: false, kind, inputText).ConfigureAwait(false);
                WritePromptResult(context, "alta.session.steered", info.Thread, runId.Value, queued: false, kind, inputText);
                return AltaExitCodes.Success;
            }

            if (options.QueueIfBusy && await runtime.HasActiveRunAsync(info.Thread, context.CancellationToken).ConfigureAwait(false))
            {
                return Unsupported(context, "session.queueUnsupported", "The target session is busy and headless prompt queueing is unavailable in the current runtime context.");
            }

            var submittedRunId = await runtime.SendAsync(
                info.Thread,
                executionOptions,
                new AgentSendOptions { Input = AgentInput.Text(inputText) },
                context.CancellationToken).ConfigureAwait(false);
            await runtime.PersistThreadLocalStateAsync(info.Thread, context.CancellationToken).ConfigureAwait(false);
            await PersistPromptProvenanceAsync(context, info.Thread, submittedRunId.Value, queued: false, kind, inputText).ConfigureAwait(false);
            WritePromptResult(context, kind is PromptDispatchKind.Message or PromptDispatchKind.Request ? "alta.session.message.sent" : "alta.session.submitted", info.Thread, submittedRunId.Value, queued: false, kind, inputText);
            return AltaExitCodes.Success;
        }
        catch (Exception ex) when (kind == PromptDispatchKind.Steer && ex is InvalidOperationException or NotSupportedException)
        {
            return Unsupported(context, "session.steerUnsupported", ex.Message);
        }
    }

    private static async ValueTask<int> HandleSessionAbortAsync(AltaCommandContext context, string? threadId, string? reason)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return UsageError(context, "usage.missingThread", "Thread id is required.", "alta session abort");
        }

        if (!context.TryGetRequired<WorkThreadRuntimeService>(nameof(WorkThreadRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = FindThread(infos, threadId);
        if (info is null)
        {
            return NotFound(context, "session.notFound", $"Session '{threadId}' was not found.");
        }

        if (!CanAccessThread(context, info.Thread))
        {
            return PermissionDenied(context, "policy.visibilityDenied", $"The caller is not allowed to abort session '{threadId}'.");
        }

        try
        {
            await runtime.AbortAsync(threadId, context.CancellationToken).ConfigureAwait(false);
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
            threadId,
            reason,
            abortedBy = CreateProvenance(context),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSessionCompactAsync(AltaCommandContext context, string? threadId, bool submit)
    {
        if (submit)
        {
            return Unsupported(context, "session.compactSubmitUnsupported", "--submit after compaction is not supported by the current runtime.");
        }

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return UsageError(context, "usage.missingThread", "Thread id is required.", "alta session compact");
        }

        if (!context.TryGetRequired<WorkThreadRuntimeService>(nameof(WorkThreadRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = FindThread(infos, threadId);
        if (info is null)
        {
            return NotFound(context, "session.notFound", $"Session '{threadId}' was not found.");
        }

        if (!CanAccessThread(context, info.Thread))
        {
            return PermissionDenied(context, "policy.visibilityDenied", $"The caller is not allowed to compact session '{threadId}'.");
        }

        var executionOptions = await BuildExecutionOptionsForThreadAsync(context, info).ConfigureAwait(false);
        await runtime.CompactAsync(info.Thread, executionOptions, context.CancellationToken).ConfigureAwait(false);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.compacted",
            version = 1,
            correlationId = context.CorrelationId,
            threadId = info.Thread.ThreadId,
            compactedBy = CreateProvenance(context),
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSkillListAsync(AltaCommandContext context, string? projectRef)
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

    private static async ValueTask<int> HandleSkillActivateAsync(AltaCommandContext context, string? skillName, string? threadId)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return UsageError(context, "usage.missingSkill", "Skill name is required.", "alta skill activate");
        }

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return UsageError(context, "usage.missingSession", "--session <thread-id> is required.", "alta skill activate");
        }

        if (!context.TryGetRequired<WorkThreadRuntimeService>(nameof(WorkThreadRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        if (infos is null)
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var info = FindThread(infos, threadId);
        if (info is null)
        {
            return NotFound(context, "session.notFound", $"Session '{threadId}' was not found.");
        }

        if (!CanAccessThread(context, info.Thread))
        {
            return PermissionDenied(context, "policy.visibilityDenied", $"The caller is not allowed to activate skills in session '{threadId}'.");
        }

        var executionOptions = await BuildExecutionOptionsForThreadAsync(context, info).ConfigureAwait(false);
        try
        {
            var runId = await runtime.ActivateSkillAsync(info.Thread, executionOptions, skillName, context.CancellationToken).ConfigureAwait(false);
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.skill.activated",
                version = 1,
                correlationId = context.CorrelationId,
                skillName,
                threadId = info.Thread.ThreadId,
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

    private static async ValueTask<int> HandleProviderListAsync(AltaCommandContext context)
    {
        var descriptors = GetBackendDescriptors(context);
        var backendIds = descriptors.Select(static descriptor => descriptor.BackendId).ToList();
        if (context.Services.Get<AgentHub>() is { } agentHub)
        {
            foreach (var id in agentHub.ListRegisteredBackends())
            {
                if (!backendIds.Contains(id))
                {
                    backendIds.Add(id);
                }
            }
        }

        foreach (var backendId in backendIds.OrderBy(static id => id.Value, StringComparer.OrdinalIgnoreCase))
        {
            var descriptor = descriptors.FirstOrDefault(candidate => candidate.BackendId == backendId);
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.provider.item",
                version = 1,
                correlationId = context.CorrelationId,
                providerKey = backendId.Value,
                backendId = backendId.Value,
                displayName = descriptor?.DisplayName,
            });
        }

        WriteSummary(context, "alta.provider.summary", backendIds.Count, truncated: false);
        await Task.CompletedTask.ConfigureAwait(false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleModelListAsync(AltaCommandContext context, string? providerFilter)
    {
        if (!context.TryGetRequired<AgentHub>(nameof(AgentHub), out var agentHub))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var providers = agentHub.ListRegisteredBackends()
            .Where(id => string.IsNullOrWhiteSpace(providerFilter) || string.Equals(id.Value, providerFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static id => id.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(providerFilter) && providers.Length == 0)
        {
            return NotFound(context, "provider.notFound", $"Provider '{providerFilter}' was not found.");
        }

        var count = 0;
        foreach (var provider in providers)
        {
            IReadOnlyList<AgentModelInfo> models;
            try
            {
                models = await agentHub.ListModelsAsync(provider, context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or NotSupportedException)
            {
                AltaJsonlWriter.WriteWarning(context.Stderr, context.CorrelationId, "model.listUnavailable", $"Provider '{provider.Value}' models are unavailable: {ex.Message}");
                continue;
            }

            foreach (var model in models.OrderBy(static model => model.Id, StringComparer.OrdinalIgnoreCase))
            {
                WriteModelItem(context, provider.Value, model);
                count++;
            }
        }

        WriteSummary(context, "alta.model.summary", count, truncated: false);
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

        if (!context.TryGetRequired<AgentHub>(nameof(AgentHub), out var agentHub))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var provider = new AgentBackendId(selection.ProviderKey);
        IReadOnlyList<AgentModelInfo> models;
        try
        {
            models = await agentHub.ListModelsAsync(provider, context.CancellationToken).ConfigureAwait(false);
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
        var result = await ResolveModelSelectionAsync(context, options).ConfigureAwait(false);
        if (result.ExitCode != AltaExitCodes.Success)
        {
            return result.ExitCode;
        }

        WriteModelSelection(context, "alta.model.selection", result.Selection!, threadId: null);
        return AltaExitCodes.Success;
    }

    private static ValueTask<int> HandlePluginList(AltaCommandContext context)
    {
        var catalog = context.Services.Get<IAltaPluginCatalog>();
        var plugins = catalog?.ListPlugins() ?? [];
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
        var queryService = context.Services.Get<IAltaSessionQueryService>() ?? new AltaSessionQueryService();
        return await queryService.LoadAsync(context).ConfigureAwait(false);
    }

    private static AltaSessionInfo? FindThread(IReadOnlyList<AltaSessionInfo> infos, string threadId)
        => infos.FirstOrDefault(info => string.Equals(info.Thread.ThreadId, threadId, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<AltaSessionInfo> GetChildren(IReadOnlyList<AltaSessionInfo> infos, AltaSessionInfo parent, bool recursive)
    {
        var directChildren = infos
            .Where(info => string.Equals(info.Thread.ParentThreadId, parent.Thread.ThreadId, StringComparison.OrdinalIgnoreCase))
            .Where(info => string.Equals(info.Thread.ProjectRef, parent.Thread.ProjectRef, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static info => info.Thread.LastActiveAt)
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

    private static WorkThreadExecutionOptions BuildExecutionOptions(
        AltaCommandContext context,
        AltaModelSelection selection,
        string workingDirectory,
        IReadOnlyList<string> projectRoots,
        Func<string?>? sourceThreadIdProvider,
        string? sourceProjectId)
        => new()
        {
            BackendId = new AgentBackendId(selection.ProviderKey),
            ProviderKey = selection.ProviderKey,
            WorkingDirectory = workingDirectory,
            ProjectRoots = projectRoots,
            Model = selection.ModelId,
            ReasoningEffort = selection.ReasoningEffort,
            Tools = CreateAltaSessionTools(context, selection.ProviderKey, sourceThreadIdProvider, sourceProjectId, workingDirectory),
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnUserInputRequest = static (_, _) => Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>(StringComparer.Ordinal))),
        };

    private static async Task<WorkThreadExecutionOptions> BuildExecutionOptionsForThreadAsync(AltaCommandContext context, AltaSessionInfo info)
    {
        var projectRoots = new List<string>();
        var workingDirectory = info.Thread.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(info.Thread.ProjectRef) && context.Services.Get<ProjectCatalog>() is { } catalog)
        {
            var project = await catalog.GetByIdAsync(info.Thread.ProjectRef, context.CancellationToken).ConfigureAwait(false);
            if (project is not null)
            {
                workingDirectory = project.ProjectPath;
                projectRoots.Add(project.ProjectPath);
            }
        }

        return new WorkThreadExecutionOptions
        {
            BackendId = new AgentBackendId(info.Thread.BackendId),
            ProviderKey = info.Thread.ResolvedProviderKey,
            WorkingDirectory = workingDirectory,
            ProjectRoots = projectRoots,
            Model = info.Preference?.ModelId,
            ReasoningEffort = info.Preference?.ReasoningEffort,
            Tools = CreateAltaSessionTools(context, info.Thread.BackendId, () => info.Thread.ThreadId, info.Thread.ProjectRef, workingDirectory),
            OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            OnUserInputRequest = static (_, _) => Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>(StringComparer.Ordinal))),
        };
    }

    private static IReadOnlyList<AgentToolDefinition>? CreateAltaSessionTools(
        AltaCommandContext context,
        string backendId,
        Func<string?>? sourceThreadIdProvider,
        string? sourceProjectId,
        string? workingDirectory)
    {
        var policy = context.Services.Get<IAltaSessionToolBackendPolicy>();
        if (policy is null || !policy.SupportsAltaSessionTool(backendId))
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
                    SourceThreadIdProvider = sourceThreadIdProvider,
                    SourceProjectId = sourceProjectId,
                    WorkingDirectory = workingDirectory,
                    DefaultMaxOutputRecords = 200,
                    DefaultMaxOutputBytes = 64 * 1024,
                    DefaultTimeout = TimeSpan.FromSeconds(120),
                }),
        ];
    }

    private static async Task<ModelResolutionResult> ResolveModelSelectionAsync(AltaCommandContext context, AltaModelSelectionOptions request)
    {
        if (!string.IsNullOrWhiteSpace(request.ModelRef))
        {
            if (!AltaModelRef.TryParse(request.ModelRef, out var parsed, out var error))
            {
                return ModelResolutionResult.Fail(UsageError(context, "usage.invalidModelRef", error!, "alta model resolve"));
            }

            return ModelResolutionResult.Success(ApplyModelOverrides(parsed, request));
        }

        AltaModelSelection? inherited = null;
        if (!string.IsNullOrWhiteSpace(request.SameModelAsThreadId))
        {
            inherited = await ResolveThreadModelSelectionAsync(context, request.SameModelAsThreadId).ConfigureAwait(false);
            if (inherited is null)
            {
                return ModelResolutionResult.Fail(NotFound(context, "session.notFound", $"Session '{request.SameModelAsThreadId}' was not found."));
            }
        }
        else if (!string.IsNullOrWhiteSpace(context.Caller.SourceThreadId))
        {
            inherited = await ResolveThreadModelSelectionAsync(context, context.Caller.SourceThreadId).ConfigureAwait(false);
        }

        var providerKey = FirstNonEmpty(request.ProviderKey, inherited?.ProviderKey, GetDefaultProviderKey(context));
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return ModelResolutionResult.Fail(NotFound(context, "provider.notFound", "No provider/backend is registered or selected."));
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
        return ModelResolutionResult.Success(selection);
    }

    private static AltaModelSelection ApplyModelOverrides(AltaModelSelection parsed, AltaModelSelectionOptions request)
    {
        var providerKey = FirstNonEmpty(request.ProviderKey, parsed.ProviderKey)!;
        var modelId = FirstNonEmpty(request.ModelId, parsed.ModelId);
        var reasoning = request.ReasoningEffort ?? parsed.ReasoningEffort;
        return new AltaModelSelection
        {
            ProviderKey = providerKey,
            ModelId = modelId,
            ReasoningEffort = reasoning,
            ModelRef = AltaModelRef.Format(providerKey, modelId, reasoning),
        };
    }

    private static async Task<AltaModelSelection?> ResolveThreadModelSelectionAsync(AltaCommandContext context, string threadId)
    {
        var infos = await LoadSessionInfosAsync(context).ConfigureAwait(false);
        var info = infos is null ? null : FindThread(infos, threadId);
        return info is null ? null : CreateModelSelection(info.Thread, info.Preference);
    }

    private static string? GetDefaultProviderKey(AltaCommandContext context)
    {
        if (GetBackendDescriptors(context).FirstOrDefault() is { } descriptor)
        {
            return descriptor.BackendId.Value;
        }

        return context.Services.Get<AgentHub>()?.ListRegisteredBackends().FirstOrDefault().Value;
    }

    private static IReadOnlyList<AgentBackendDescriptor> GetBackendDescriptors(AltaCommandContext context)
        => context.Services.Get<IReadOnlyList<AgentBackendDescriptor>>() ?? [];

    private static AltaModelSelection CreateModelSelection(WorkThreadDescriptor thread, WorkThreadPreference? preference)
    {
        var providerKey = thread.ResolvedProviderKey;
        var modelId = preference?.ModelId;
        var reasoning = preference?.ReasoningEffort;
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

    private static string CommandPathForPrompt(PromptDispatchKind kind)
        => kind switch
        {
            PromptDispatchKind.Steer => "alta session steer",
            PromptDispatchKind.Queue => "alta session queue",
            PromptDispatchKind.Message => "alta session message",
            PromptDispatchKind.Request => "alta session request",
            _ => "alta session send",
        };

    private static async Task PersistThreadPreferenceAsync(AltaCommandContext context, string threadId, AltaModelSelection selection)
    {
        if (context.Services.Get<WorkThreadCatalog>() is not { } threadCatalog)
        {
            return;
        }

        var viewState = await threadCatalog.LoadViewStateAsync(context.CancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(selection.ModelId) && selection.ReasoningEffort is null)
        {
            viewState.ThreadPreferences.Remove(threadId);
        }
        else
        {
            viewState.ThreadPreferences[threadId] = new WorkThreadPreference
            {
                ModelId = selection.ModelId,
                ReasoningEffort = selection.ReasoningEffort,
            };
        }

        viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await threadCatalog.SaveViewStateAsync(viewState, context.CancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistPromptProvenanceAsync(
        AltaCommandContext context,
        WorkThreadDescriptor thread,
        string? runId,
        bool queued,
        PromptDispatchKind kind,
        string prompt)
    {
        if (context.Services.Get<WorkThreadCatalog>() is not { } threadCatalog)
        {
            return;
        }

        var viewState = await threadCatalog.LoadViewStateAsync(context.CancellationToken).ConfigureAwait(false);
        var localState = viewState.ThreadStates.TryGetValue(thread.ThreadId, out var existingState)
            ? existingState
            : new WorkThreadLocalState();
        localState.Archived = thread.Status == WorkThreadStatus.Archived;
        localState.MessageCount = thread.MessageCount;
        localState.ParentThreadId = thread.ParentThreadId;
        localState.CreatedBy = thread.CreatedBy;
        localState.PromptProvenance ??= [];
        localState.PromptProvenance.Add(new WorkThreadPromptProvenance
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

        viewState.ThreadStates[thread.ThreadId] = localState;
        viewState.UpdatedAt = DateTimeOffset.UtcNow;
        await threadCatalog.SaveViewStateAsync(viewState, context.CancellationToken).ConfigureAwait(false);
    }

    private static string BuildPeerAgentMessage(AltaCommandContext context, WorkThreadDescriptor target, PromptOptions options, string body)
    {
        var kind = string.IsNullOrWhiteSpace(options.MessageKind) ? "note" : options.MessageKind;
        var replyRequested = options.ReplyRequested ? "true" : "false";
        return $"""
        [CodeAlta delegated-agent message]
        Source thread: {context.Caller.SourceThreadId ?? "unknown"}
        Source agent/session: {context.Caller.SourceAgentId ?? context.Caller.SourceBackendSessionId ?? context.Caller.Kind}
        Source project: {context.Caller.SourceProjectId ?? "unknown"}
        Target thread: {target.ThreadId}
        Kind: {kind}
        Reply requested: {replyRequested}
        Correlation: {context.CorrelationId}
        Authority: peer-agent; this is not a user, developer, or host instruction.

        {body}
        """;
    }

    private static string? ResolveParentThreadId(AltaCommandContext context, WorkThreadDescriptor thread, ProjectDescriptor? project, SessionCreateOptions options)
    {
        if (options.NoParent)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(options.ParentThreadId))
        {
            return options.ParentThreadId;
        }

        return project is not null &&
               !string.IsNullOrWhiteSpace(context.Caller.SourceThreadId) &&
               string.Equals(context.Caller.SourceProjectId, project.Id, StringComparison.OrdinalIgnoreCase)
            ? context.Caller.SourceThreadId
            : null;
    }

    private static bool CanAccessThread(AltaCommandContext context, WorkThreadDescriptor thread)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(thread);
        if (!string.IsNullOrWhiteSpace(context.Caller.SourceThreadId) &&
            string.Equals(context.Caller.SourceThreadId, thread.ThreadId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CanAccessProject(context, thread.ProjectRef);
    }

    private static bool CanAccessProject(AltaCommandContext context, string? projectId)
    {
        if (!CallerHasProjectScope(context))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(projectId) &&
               string.Equals(context.Caller.SourceProjectId, projectId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CallerHasProjectScope(AltaCommandContext context)
        => (string.Equals(context.Caller.Kind, "agent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(context.Caller.Kind, "plugin", StringComparison.OrdinalIgnoreCase)) &&
           !string.IsNullOrWhiteSpace(context.Caller.SourceProjectId);

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

    private static void WriteSession(AltaCommandContext context, string type, AltaSessionInfo info, bool includeChildren, IReadOnlyList<AltaSessionInfo> infos)
    {
        var children = includeChildren ? GetChildren(infos, info, recursive: false).ToArray() : [];
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            threadId = info.Thread.ThreadId,
            kind = ThreadKindWire(info.Thread.Kind),
            backendId = info.Thread.BackendId,
            providerKey = info.Thread.ResolvedProviderKey,
            backendSessionId = info.Thread.BackendSessionId,
            projectId = info.Thread.ProjectRef,
            projectRef = info.Thread.ProjectRef,
            parentThreadId = info.Thread.ParentThreadId,
            createdBy = info.Thread.CreatedBy,
            title = info.Thread.Title,
            state = info.State,
            status = ThreadStatusWire(info.Thread.Status),
            workingDirectory = info.Thread.WorkingDirectory,
            latestSummary = info.Thread.LatestSummary,
            messageCount = info.Thread.MessageCount,
            isRunning = info.IsRunning,
            queuedPromptCount = 0,
            modelSelection = ToModelSelectionPayload(CreateModelSelection(info.Thread, info.Preference)),
            createdAt = info.Thread.CreatedAt,
            updatedAt = info.Thread.UpdatedAt,
            lastActiveAt = info.Thread.LastActiveAt,
            startedAt = info.Thread.StartedAt,
            sourcePath = info.Thread.SourcePath,
            childCount = includeChildren ? children.Length : (int?)null,
            childThreadIds = includeChildren ? children.Select(static child => child.Thread.ThreadId).ToArray() : null,
        });
    }

    private static void WriteModelSelection(AltaCommandContext context, string type, AltaModelSelection selection, string? threadId)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            threadId,
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

    private static void WriteAgentEvent(AltaCommandContext context, WorkThreadDescriptor thread, AgentEvent agentEvent, long sequenceNumber)
    {
        var mapped = MapAgentEvent(agentEvent);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.session.event",
            version = 1,
            correlationId = context.CorrelationId,
            threadId = thread.ThreadId,
            sequenceNumber,
            timestamp = agentEvent.Timestamp,
            kind = mapped.Kind,
            role = mapped.Role,
            source = new
            {
                kind = "backend",
                backendId = agentEvent.BackendId.Value,
                backendSessionId = agentEvent.SessionId,
                runId = agentEvent.RunId?.Value,
            },
            content = string.IsNullOrEmpty(mapped.Text) ? [] : new[] { new { type = "text", text = mapped.Text } },
            metadata = mapped.Metadata,
        });
    }

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

    private static bool IncludesEvent(AgentEvent agentEvent, IReadOnlySet<string> includes)
    {
        if (includes.Count == 0)
        {
            return true;
        }

        var mapped = MapAgentEvent(agentEvent);
        return includes.Contains(mapped.Role) ||
               mapped.Kind.Split('.', 2)[0] is { } prefix && includes.Contains(prefix) ||
               (mapped.Role == "host" && includes.Contains("event"));
    }

    private static IReadOnlySet<string> ParseIncludes(string? include)
    {
        if (string.IsNullOrWhiteSpace(include))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in include.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            set.Add(part);
        }

        return set;
    }

    private static void WritePromptResult(AltaCommandContext context, string type, WorkThreadDescriptor thread, string runId, bool queued, PromptDispatchKind kind, string prompt)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            threadId = thread.ThreadId,
            runId,
            queued,
            dispatchKind = kind.ToString().ToLowerInvariant(),
            submittedBy = CreateProvenance(context),
            promptPreview = prompt.Length <= 160 ? prompt : prompt[..160],
        });
    }

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

    private static void WriteModelItem(AltaCommandContext context, string providerKey, AgentModelInfo model, AgentReasoningEffort? requestedReasoning = null)
    {
        var reasoning = requestedReasoning ?? model.DefaultReasoningEffort;
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
            modelRef = AltaModelRef.Format(providerKey, model.Id, reasoning),
            capabilities = model.Capabilities,
        });
    }

    private static void WritePlugin(AltaCommandContext context, string type, AltaPluginSummary plugin)
    {
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type,
            version = 1,
            correlationId = context.CorrelationId,
            plugin.RuntimeKey,
            plugin.DisplayName,
            plugin.Version,
            plugin.Scope,
            plugin.State,
            plugin.Diagnostics,
        });
    }

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
            SourceThreadId = context.Caller.SourceThreadId,
            SourceBackendSessionId = context.Caller.SourceBackendSessionId,
            SourceProjectId = context.Caller.SourceProjectId,
            SourceAgentId = context.Caller.SourceAgentId,
            PluginRuntimeKey = context.Caller.PluginRuntimeKey,
            CorrelationId = context.CorrelationId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string ThreadKindWire(WorkThreadKind kind)
        => kind switch
        {
            WorkThreadKind.GlobalThread => "global_thread",
            WorkThreadKind.ProjectThread => "project_thread",
            WorkThreadKind.InternalThread => "internal_thread",
            _ => kind.ToString().ToLowerInvariant(),
        };

    private static string ThreadStatusWire(WorkThreadStatus status)
        => status.ToString().ToLowerInvariant();

    private sealed class AltaModelSelectionOptions
    {
        public string? ModelRef { get; set; }

        public string? SameModelAsThreadId { get; set; }

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

        public string? ParentThreadId { get; set; }

        public bool NoParent { get; set; }
    }

    private sealed class PromptOptions
    {
        public string? Message { get; set; }

        public bool UseStdin { get; set; }

        public bool QueueIfBusy { get; set; }

        public string? MessageKind { get; set; }

        public bool ReplyRequested { get; set; }
    }

    private sealed record MappedEvent(string Kind, string Role, string? Text, object? Metadata);

    private sealed record ModelResolutionResult(int ExitCode, AltaModelSelection? Selection)
    {
        public static ModelResolutionResult Success(AltaModelSelection selection) => new(AltaExitCodes.Success, selection);

        public static ModelResolutionResult Fail(int exitCode) => new(exitCode, null);
    }

    private sealed record SkillQueryResult(int ExitCode, SkillCatalogQuery? Query)
    {
        public static SkillQueryResult Success(SkillCatalogQuery query) => new(AltaExitCodes.Success, query);

        public static SkillQueryResult Fail(int exitCode) => new(exitCode, null);
    }

    private sealed record PromptReadResult(int ExitCode, string? Prompt)
    {
        public static PromptReadResult Success(string prompt) => new(AltaExitCodes.Success, prompt);

        public static PromptReadResult Fail(int exitCode) => new(exitCode, null);
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
