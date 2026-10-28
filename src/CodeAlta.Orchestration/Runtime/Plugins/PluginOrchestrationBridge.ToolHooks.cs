using CodeAlta.Agent;
using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Runtime.Plugins;

/// <summary>What the tool hooks of a session are for: the session, its project, its folder and its space.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="ProjectId">The project of the session, or <see langword="null"/> for a global session.</param>
/// <param name="WorkingDirectory">The folder the session works in.</param>
/// <param name="SpaceId">The space of the session; <see langword="null"/> applies no space.</param>
public sealed record SessionToolHookScope(string SessionId, string? ProjectId, string? WorkingDirectory, string? SpaceId);

public sealed partial class PluginOrchestrationBridge
{
    /// <summary>
    /// Creates the tool hooks of a session: every tool call the session runs, or that its agent CLI asks about, goes
    /// through the <see cref="PluginBase.OnToolCallAsync"/> and <see cref="PluginBase.OnToolResultAsync"/> of the plugins
    /// running when it is made, in their order.
    /// </summary>
    /// <param name="scope">The session.</param>
    /// <returns>The hooks, which do nothing while no plugin hooks tool calls.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    public AgentToolInterceptor CreateToolInterceptor(SessionToolHookScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new PluginToolInterceptor(this, scope);
    }

    private sealed class PluginToolInterceptor(PluginOrchestrationBridge bridge, SessionToolHookScope scope) : AgentToolInterceptor
    {
        public override async ValueTask<AgentToolCallDecision> BeforeToolCallAsync(AgentToolHookCall call, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(call);
            var activePlugins = bridge._getActivePlugins();
            if (!PluginContributionAdapterService.HasToolHooks(activePlugins)) return AgentToolCallDecision.Allow;
            var seed = activePlugins[0];
            var (kind, plugin) = KindOf(call);
            var template = new PluginToolCallContext
            {
                Plugin = seed.Descriptor,
                Services = seed.RuntimeContext.Services,
                Invocation = call.Invocation,
                ToolKind = kind,
                ToolPluginId = plugin,
                WorkingDirectory = call.WorkingDirectory ?? scope.WorkingDirectory,
                ShellCommand = call.ShellCommand is { } shell ? new PluginShellCommand(shell.Command, shell.WorkingDirectory) : null,
                ModifiedFiles = call.ModifiedFiles,
                ParentToolCallId = call.Invocation.ParentToolCallId,
                Capabilities = (PluginToolHookCapabilities)(int)call.Capabilities,
            };
            var result = await bridge._adapter.RunToolCallHooksAsync(activePlugins, template, OptionsOf(call), cancellationToken).ConfigureAwait(false);
            if (result.BlockMessage is { } blocked) return AgentToolCallDecision.Block(blocked);
            return result.Result is { Disposition: PluginToolCallDisposition.ReplaceArguments, ReplacementArguments: { } arguments }
                ? AgentToolCallDecision.WithArguments(arguments)
                : AgentToolCallDecision.Allow;
        }

        public override async ValueTask<AgentToolResult> AfterToolCallAsync(AgentToolHookCall call, AgentToolResult result, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(call);
            ArgumentNullException.ThrowIfNull(result);
            var activePlugins = bridge._getActivePlugins();
            if (!PluginContributionAdapterService.HasToolHooks(activePlugins)) return result;
            var seed = activePlugins[0];
            var (kind, plugin) = KindOf(call);
            var template = new PluginToolResultContext
            {
                Plugin = seed.Descriptor,
                Services = seed.RuntimeContext.Services,
                Invocation = call.Invocation,
                Result = result,
                ToolKind = kind,
                ToolPluginId = plugin,
                WorkingDirectory = call.WorkingDirectory ?? scope.WorkingDirectory,
                ShellCommand = call.ShellCommand is { } shell ? new PluginShellCommand(shell.Command, shell.WorkingDirectory) : null,
                ModifiedFiles = call.ModifiedFiles,
                ParentToolCallId = call.Invocation.ParentToolCallId,
                Capabilities = (PluginToolHookCapabilities)(int)call.Capabilities,
            };
            return (await bridge._adapter.RunToolResultHooksAsync(activePlugins, template, OptionsOf(call), cancellationToken).ConfigureAwait(false)).Result;
        }

        private (PluginToolKind Kind, string? Plugin) KindOf(AgentToolHookCall call)
        {
            if (call.RunByAgent) return (PluginToolKind.Agent, null);
            var plugin = call.Spec is { } spec ? bridge._adapter.FindToolPlugin(spec) : null;
            return plugin switch
            {
                null => (PluginToolKind.BuiltIn, null),
                "mcp" => (PluginToolKind.Mcp, plugin),
                _ => (PluginToolKind.Plugin, plugin),
            };
        }

        private PluginAdapterOperationOptions OptionsOf(AgentToolHookCall call)
            => MarkHeadless(new PluginAdapterOperationOptions
            {
                ProjectId = scope.ProjectId,
                ProjectPath = scope.WorkingDirectory,
                SessionId = string.IsNullOrWhiteSpace(call.Invocation.SessionId) ? scope.SessionId : call.Invocation.SessionId,
                SpaceId = scope.SpaceId,
                ProviderId = call.Invocation.ProviderId.Value,
                IsCodeAltaManagedProvider = IsCodeAltaManagedProvider(call.Invocation.ProviderId),
            });
    }
}
