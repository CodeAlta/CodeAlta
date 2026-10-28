using CodeAlta.Agent.Runtime.Tools;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.Plugins;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    /// <summary>
    /// Gets or sets what makes the tool hooks of a session (those of the plugins, <see cref="PluginOrchestrationBridge.CreateToolInterceptor"/>):
    /// they run before and after each tool call of the session, before its guards, its permission prompt and its sandbox.
    /// Read when a session is attached; <see langword="null"/>, the default, gives sessions no tool hook.
    /// </summary>
    public Func<SessionToolHookScope, AgentToolInterceptor?>? ToolInterceptorOf { get; set; }

    private async ValueTask<AgentToolInterceptor?> CreateToolInterceptorAsync(SessionViewDescriptor session, ProjectDescriptor? project,
        string? workingDirectory, CancellationToken cancellationToken)
    {
        if (ToolInterceptorOf is not { } create) return null;
        string? spaceId;
        try
        {
            spaceId = (await ResolveSpaceAsync(session.SpaceId, project, cancellationToken).ConfigureAwait(false)).Id;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Spaces that cannot be read limit no plugin, as for the other contributions of plugins.
            spaceId = null;
        }

        return create(new SessionToolHookScope(session.SessionId, project?.Id, workingDirectory, spaceId));
    }
}
