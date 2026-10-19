using CodeAlta.Agent;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// The identity a plugin works under for a session: the one of the session's space (its Git author, its GitHub
/// profile, its Jira account), as the session attached with it. It holds only references, never a secret.
/// </summary>
public sealed record PluginIdentity
{
    /// <summary>Gets the identity that changes nothing: the user's usual settings.</summary>
    public static PluginIdentity Usual { get; } = new();

    /// <summary>Gets the space the identity is of, when the host knows it.</summary>
    public string? SpaceId { get; init; }

    /// <summary>
    /// Gets the variables a process started for the session runs with, over the environment of the application,
    /// such as <c>GH_CONFIG_DIR</c> for the GitHub CLI.
    /// </summary>
    public AgentProcessEnvironment Environment { get; init; } = AgentProcessEnvironment.Empty;

    /// <summary>Gets the Jira site of the space, such as <c>contoso.atlassian.net</c>; null for none.</summary>
    public string? JiraSite { get; init; }

    /// <summary>Gets the email of the Jira account of the space; null for none.</summary>
    public string? JiraEmail { get; init; }

    /// <summary>Gets the name of the environment variable that holds the Jira API token of the space, never the token; null for none.</summary>
    public string? JiraTokenVariable { get; init; }
}

/// <summary>
/// Gives plugins the identity of the work they do: a session's, the one of the space the window shows, or the
/// one of a project that no session works on (the background work of an automation). A host without spaces gives
/// <see cref="PluginIdentity.Usual"/>.
/// </summary>
public interface IPluginIdentityService
{
    /// <summary>Gets the identity of the work of a session, or of the window when no session asks.</summary>
    /// <param name="sessionId">The session; null for what the window shows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; <see cref="PluginIdentity.Usual"/> when nothing else applies.</returns>
    ValueTask<PluginIdentity> GetIdentityAsync(string? sessionId, CancellationToken cancellationToken);

    /// <summary>Gets the identity of work on a project that no session does: the one of the space its new sessions go to.</summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The identity; <see cref="PluginIdentity.Usual"/> when nothing else applies.</returns>
    ValueTask<PluginIdentity> GetProjectIdentityAsync(string projectPath, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the identity of the work of the window on a project when no session asks, such as its issues: the one of
    /// the space the window shows, with what the configuration of the project gives its sessions over it (its CLI
    /// profiles), as a terminal of the window listed under the project has it.
    /// </summary>
    /// <param name="projectPath">The folder of the project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The identity; by default the one of the window, <see cref="GetIdentityAsync(string?, CancellationToken)"/> without session.
    /// </returns>
    ValueTask<PluginIdentity> GetWindowIdentityAsync(string projectPath, CancellationToken cancellationToken)
        => GetIdentityAsync(null, cancellationToken);
}

/// <summary>The identity service of a host without spaces: every work keeps the usual settings.</summary>
public sealed class NoopPluginIdentityService : IPluginIdentityService
{
    private NoopPluginIdentityService()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static NoopPluginIdentityService Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<PluginIdentity> GetIdentityAsync(string? sessionId, CancellationToken cancellationToken)
        => ValueTask.FromResult(PluginIdentity.Usual);

    /// <inheritdoc />
    public ValueTask<PluginIdentity> GetProjectIdentityAsync(string projectPath, CancellationToken cancellationToken)
        => ValueTask.FromResult(PluginIdentity.Usual);
}
