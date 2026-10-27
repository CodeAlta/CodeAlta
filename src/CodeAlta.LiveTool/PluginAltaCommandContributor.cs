using System.Collections.Frozen;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.LiveTool;

/// <summary>
/// Adapts trusted plugin-provided alta command factories into the in-process command registry.
/// </summary>
public sealed class PluginAltaCommandContributor : IAltaCommandContributor
{
    private const string UnresolvedProjectScopeId = "__codealta_unresolved_project_scope__";

    private static readonly FrozenSet<string> ReservedRootCommands = new[]
    {
        "version",
        "ask",
        "project",
        "space",
        "session",
        "sandbox",
        "skill",
        "skills",
        "skills_activate",
        "provider",
        "model",
        "plugin",
        "tool",
        "canvas",
        "landing",
        "documentation",
        "search",
        "window",
        // Built-in commands that some hosts do not have (the views of the window), and the others a sandboxed session
        // may run (AltaSandboxGate): a plugin never takes their name, which the gate admits by name.
        "notes",
        "note",
        "reminder",
        "job",
        "task",
        "plan",
        "review",
        "diff",
        "editor",
        "prompt",
        "issue",
        "issues",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Roots of plugin commands a sandboxed session may run, and the plugin that alone may give each of them: its type,
    // in its assembly.
    private static readonly FrozenDictionary<string, (string Assembly, string Type)> SandboxRootOwners = new Dictionary<string, (string Assembly, string Type)>(StringComparer.OrdinalIgnoreCase)
    {
        ["mcp"] = ("CodeAlta.Plugin.Mcp", "CodeAlta.Plugin.Mcp.McpPlugin"),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IEnumerable<CommandNode> CreateCommandLineNodes(AltaCommandContributionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var catalog = context.Invocation.Services.Get<IAltaPluginCatalog>();
        if (catalog is null)
        {
            yield break;
        }

        var usedRoots = new HashSet<string>(ReservedRootCommands, StringComparer.OrdinalIgnoreCase);
        foreach (var contribution in FilterContributions(catalog.ListCommandContributions(), usedRoots))
        {
            // Factories run before the parser identifies a target and can execute plugin code.
            // Keep this unknown-target claim through the original selected command callback too.
            context.Invocation.ClaimUnknownCommandWork();
            var pluginContext = CreatePluginContext(context.Invocation, contribution);
            var node = contribution.Command.CreateCommandNode(pluginContext);
            if (node is not Command command ||
                !RootMatchesDeclaredPath(command.Name, contribution.Command.Path))
            {
                continue;
            }

            yield return command;
        }
    }

    /// <inheritdoc />
    public IEnumerable<AltaCommandPolicy> GetCommandPolicies(AltaCommandContributionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var catalog = context.Invocation.Services.Get<IAltaPluginCatalog>();
        if (catalog is null)
        {
            yield break;
        }

        var usedRoots = new HashSet<string>(ReservedRootCommands, StringComparer.OrdinalIgnoreCase);
        foreach (var contribution in FilterContributions(catalog.ListCommandContributions(), usedRoots))
        {
            yield return new AltaCommandPolicy
            {
                Path = contribution.Command.Path,
                RequiresInProcessRuntime = contribution.Command.Policy.RequiresInProcessRuntime,
                IsMutating = contribution.Command.Policy.IsMutating,
                IsDisruptive = contribution.Command.Policy.IsDisruptive,
                SupportsCatalogOnlyContext = contribution.Command.Policy.SupportsCatalogOnlyContext,
            };
        }
    }

    private static IEnumerable<AltaPluginCommandContribution> FilterContributions(
        IEnumerable<AltaPluginCommandContribution> contributions,
        HashSet<string> usedRoots)
    {
        foreach (var contribution in contributions
                     .Where(static contribution => !string.IsNullOrWhiteSpace(contribution.Command.Path))
                     .OrderBy(static contribution => contribution.Command.Order)
                     .ThenBy(static contribution => contribution.Command.Path, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static contribution => contribution.Plugin.RuntimeKey, StringComparer.OrdinalIgnoreCase))
        {
            var root = GetRoot(contribution.Command.Path);
            if (root is null
                || SandboxRootOwners.TryGetValue(root, out var owner)
                && !(string.Equals(contribution.Plugin.AssemblyName, owner.Assembly, StringComparison.Ordinal) && string.Equals(contribution.Plugin.TypeName, owner.Type, StringComparison.Ordinal))
                || !usedRoots.Add(root))
            {
                continue;
            }

            yield return contribution;
        }
    }

    private static PluginAltaCommandContext CreatePluginContext(
        AltaCommandContext invocation,
        AltaPluginCommandContribution contribution)
        => new()
        {
            Plugin = contribution.Plugin,
            Services = CreatePluginScopedServices(contribution.Services, contribution),
            Scope = contribution.Scope,
            ScopeProjectId = contribution.ScopeProjectId,
            ScopeProjectPath = contribution.ScopeProjectPath,
            CorrelationId = invocation.CorrelationId,
            WorkingDirectory = invocation.Cwd,
            SourceSessionId = invocation.Caller.SourceSessionId,
            SourceProjectId = invocation.Caller.SourceProjectId,
            SourceAgentId = invocation.Caller.SourceAgentId,
            ResolveSourceSpaceIdAsync = invocation.Services.Get<SpaceCatalog>() is { } spaces
                ? async _ => (await BuiltInAltaCommandContributor.CallerSpaceAsync(invocation, spaces).ConfigureAwait(false))?.Id
                : null,
            ResolveSourceCommandRefusalAsync = (what, _) => BuiltInAltaCommandContributor.CommandRefusalAsync(invocation, what),
            ResolveSourceSandboxMcpServersAsync = async _ => (await AltaSandboxGate.OfCallerAsync(invocation).ConfigureAwait(false))?.McpServers,
            // An agent of a session asks on the card of its session; no other caller has one to be asked on.
            RequestPermissionAsync = invocation.Caller.Kind == "agent" && invocation.Caller.SourceSessionId is { Length: > 0 } sessionId
                && invocation.Services.Get<CodeAlta.Orchestration.Runtime.SessionRuntimeService>() is { } runtime
                ? async (request, token) => await runtime.Permissions.RequestForSessionAsync(sessionId, request, token).ConfigureAwait(false)
                : null,
            RunTools = invocation.Caller.Kind == "agent" ? invocation.Caller.RunTools : null,
            AttachApp = invocation.Caller.Kind == "agent" ? invocation.AttachApp : null,
            Stdin = invocation.Stdin,
            Stdout = invocation.Stdout,
            Stderr = invocation.Stderr,
            CancellationToken = invocation.CancellationToken,
        };

    // Always wrapped, whatever the alta service: the services of a command an agent runs never answer asks.
    private static IPluginServices CreatePluginScopedServices(IPluginServices services, AltaPluginCommandContribution contribution)
        => new PluginScopedServices(services, services.Alta is IPluginAltaRuntimeService runtimeService ? new PluginScopedAltaService(runtimeService, contribution) : services.Alta);

    private static bool RootMatchesDeclaredPath(string commandName, string path)
        => string.Equals(commandName, GetRoot(path), StringComparison.OrdinalIgnoreCase);

    private static string? GetRoot(string path)
    {
        var segments = path.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length == 0 ? null : segments[0];
    }

    private sealed class PluginScopedAltaService(IPluginAltaRuntimeService inner, AltaPluginCommandContribution contribution) : IPluginAltaService
    {
        public ValueTask<PluginAltaCommandResult> InvokeAsync(
            IReadOnlyList<string> args,
            string? stdin = null,
            PluginAltaInvocationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var effectiveOptions = contribution.Scope == PluginScope.Project
                ? (options ?? new PluginAltaInvocationOptions()) with
                {
                    SourceProjectId = string.IsNullOrWhiteSpace(contribution.ScopeProjectId)
                        ? UnresolvedProjectScopeId
                        : contribution.ScopeProjectId,
                    WorkingDirectory = options?.WorkingDirectory ?? contribution.ScopeProjectPath,
                }
                : options;
            return inner.InvokeAsync(contribution.Plugin.RuntimeKey, args, stdin, effectiveOptions, cancellationToken);
        }
    }

    private sealed class PluginScopedServices(IPluginServices inner, IPluginAltaService alta) : IPluginServices
    {
        public XenoAtom.Logging.Logger Logger => inner.Logger;

        public IPluginUiService Ui => inner.Ui;

        public IPluginStateStore State => inner.State;

        public IPluginDatabase Database => inner.Database;

        public IPluginWorkspaceService Workspace => inner.Workspace;

        public IPluginSessionService Sessions => inner.Sessions;

        public IPluginPromptService Prompts => inner.Prompts;

        public IPluginAgentService Agents => inner.Agents;

        public IPluginTaskService Tasks => inner.Tasks;

        public IPluginAltaService Alta { get; } = alta;

        public IPluginCanvasService Canvases => inner.Canvases;

        public IPluginIdentityService Identity => inner.Identity;

        public IPluginSpaceService Spaces => inner.Spaces;

        public IPluginAppFileService AppFiles => inner.AppFiles;

        // An agent runs these commands: through them it would answer what it asked the user.
        public IPluginAskService Asks => NoopPluginAskService.Instance;
    }
}
