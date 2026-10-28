using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.Plugins;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class OwnedSessionCommandService
{
    /// <summary>The code of a compaction a plugin cancelled; the reason it gave is in the timeline of the session.</summary>
    internal const string CompactPluginCancelledCode = "compact_plugin_cancelled";

    // The compaction hooks of plugins (GetCompactionContributions) around a compaction this owner runs: a session that no
    // plugin hooks compacts as it would without plugins. Only these manual compactions are hooked: the ones a session makes
    // by itself, inside a run whose context is full, are part of that run and cannot wait for a plugin or be refused.
    private async Task<CompactionHooks?> BeforeCompactionHooksAsync(PluginOrchestrationBridge plugins, CompactOperation operation)
    {
        var token = operation.Execution.Token;
        var session = await _runtime.ResolveOwnedSessionAsync(operation.Request.SessionId, token).ConfigureAwait(false);
        // A session that is no longer owned is refused by the compaction itself.
        if (session is null || !string.Equals(session.SessionId, operation.Request.SessionId, StringComparison.OrdinalIgnoreCase)) return null;
        var project = string.IsNullOrWhiteSpace(session.ProjectRef) ? null
            : await _projects.GetByIdAsync(session.ProjectRef, token).ConfigureAwait(false);
        var spaceId = (await _runtime.ResolveSpaceAsync(session.SpaceId, project, token).ConfigureAwait(false)).Id;
        if (!plugins.HasCompactionHooks(session, spaceId)) return null;
        var hooks = new CompactionHooks(plugins, session, spaceId, operation.Receipt.OperationId.ToString("N"));
        hooks.Preparation = await plugins.BeforeManualCompactionAsync(session, spaceId, hooks.CompactionId, token).ConfigureAwait(false);
        return hooks;
    }

    // The reason of a plugin is its own text: the receipt keeps a stable code, the timeline says why.
    private async Task RecordPluginCancelledCompactionAsync(CompactionHooks hooks, string reason)
    {
        try
        {
            await _runtime.AppendSessionEventAsync(hooks.Session, new AgentSessionUpdateEvent(new ModelProviderId(hooks.Session.ResolvedProviderKey),
                hooks.Session.SessionId, DateTimeOffset.UtcNow, null, AgentSessionUpdateKind.Warning,
                $"A plugin cancelled the compaction: {reason}"), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or IOException or UnauthorizedAccessException)
        {
            // A session that is being removed, or a runtime that closes, keeps no record.
        }
    }

    private static async Task AfterCompactionHooksAsync(CompactionHooks hooks, bool succeeded, string? summary, CancellationToken cancellationToken)
    {
        try
        {
            await hooks.Plugins.AfterManualCompactionAsync(hooks.Session, hooks.SpaceId, hooks.CompactionId, succeeded, summary, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The compaction settled and its receipt must say how, whatever the hooks do: the owner may be closing, and
            // the adapter already keeps the failure of a hook as a diagnostic of its plugin.
        }
    }

    private sealed class CompactionHooks(PluginOrchestrationBridge plugins, SessionViewDescriptor session, string? spaceId, string compactionId)
    {
        internal PluginOrchestrationBridge Plugins { get; } = plugins;
        internal SessionViewDescriptor Session { get; } = session;
        internal string? SpaceId { get; } = spaceId;
        internal string CompactionId { get; } = compactionId;
        internal PluginCompactionPreparation Preparation { get; set; } = PluginCompactionPreparation.Empty;
    }
}
