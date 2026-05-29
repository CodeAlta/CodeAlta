using System.Collections.Frozen;
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
        "project",
        "session",
        "skill",
        "skills",
        "skills_activate",
        "provider",
        "model",
        "plugin",
        "tool",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
            if (root is null || !usedRoots.Add(root))
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
            Stdin = invocation.Stdin,
            Stdout = invocation.Stdout,
            Stderr = invocation.Stderr,
            CancellationToken = invocation.CancellationToken,
        };

    private static IPluginServices CreatePluginScopedServices(IPluginServices services, AltaPluginCommandContribution contribution)
    {
        if (services.Alta is not IPluginAltaRuntimeService runtimeService)
        {
            return services;
        }

        return new PluginScopedServices(services, new PluginScopedAltaService(runtimeService, contribution));
    }

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

        public IPluginWorkspaceService Workspace => inner.Workspace;

        public IPluginSessionService Sessions => inner.Sessions;

        public IPluginPromptService Prompts => inner.Prompts;

        public IPluginAgentService Agents => inner.Agents;

        public IPluginTaskService Tasks => inner.Tasks;

        public IPluginAltaService Alta { get; } = alta;
    }
}
