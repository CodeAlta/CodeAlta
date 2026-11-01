namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Gives a plugin that serves the user beyond the window, such as the page a phone opens, what sessions ask permission for:
/// the commands, the file changes and the skills that wait, and the user's answer, which the session receives as an answer from
/// the window. The answer is the user's: a plugin answers only what the user chose, and the <c>alta</c> commands, which agents
/// run, never reach this service.
/// </summary>
public interface IPluginPermissionService
{
    /// <summary>Gets whether the host has permission requests a plugin can read and answer.</summary>
    bool HasPermissions { get; }

    /// <summary>Lists the sessions whose permission requests wait for the user, with what each waits on.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One entry per session; a reading that can be stale at once.</returns>
    ValueTask<IReadOnlyList<PluginPermissionSummary>> ListWaitingAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the permission requests a session waits on, the first one to answer first.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The first requests, as the window shows them; none when the session waits for none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is null.</exception>
    ValueTask<PluginPermissionPage> ListAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Answers a permission request the user saw.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="requestId">The request, <see cref="PluginPermissionRequest.RequestId"/>.</param>
    /// <param name="decision">What the user chose.</param>
    /// <param name="cancellationToken">Cancels the wait for the host.</param>
    /// <returns>What became of the answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> or <paramref name="requestId"/> is null.</exception>
    ValueTask<PluginPermissionOutcome> ResolveAsync(string sessionId, string requestId, PluginPermissionDecision decision, CancellationToken cancellationToken = default);
}

/// <summary>What the user can answer to a permission request from a plugin.</summary>
public enum PluginPermissionDecision
{
    /// <summary>The request is allowed, this once.</summary>
    AllowOnce,

    /// <summary>The request is refused; the session goes on without it.</summary>
    Deny,
}

/// <summary>The permission requests a session waits on, as <see cref="IPluginPermissionService.ListWaitingAsync"/> lists them.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="RequestId">The first request, <see cref="PluginPermissionRequest.RequestId"/>: it changes when that one is answered.</param>
/// <param name="Count">How many requests were read for the session.</param>
public sealed record PluginPermissionSummary(string SessionId, string RequestId, int Count);

/// <summary>The first permission requests of a session.</summary>
/// <param name="Requests">The requests, in the order they are answered.</param>
/// <param name="HasMore">Whether more requests wait after them.</param>
public sealed record PluginPermissionPage(IReadOnlyList<PluginPermissionRequest> Requests, bool HasMore);

/// <summary>A permission request, with what the window shows of it.</summary>
public sealed record PluginPermissionRequest
{
    /// <summary>Gets the id of the request.</summary>
    public required string RequestId { get; init; }

    /// <summary>Gets what is asked: <c>commandExecution</c>, <c>fileChange</c> or <c>skill</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the command of a <c>commandExecution</c>; <see langword="null"/> otherwise.</summary>
    public string? Command { get; init; }

    /// <summary>Gets the folder of the command of a <c>commandExecution</c>; <see langword="null"/> otherwise.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the folder that holds the files of a <c>fileChange</c>; <see langword="null"/> otherwise.</summary>
    public string? GrantRoot { get; init; }

    /// <summary>Gets what the agent says of the request, or <see langword="null"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets whether the request is too long to show in full: allowing it allows more than what is shown.</summary>
    public bool Shortened { get; init; }

    /// <summary>Gets whether a sandboxed session asks to run the command outside its sandbox.</summary>
    public bool OutsideSandbox { get; init; }

    /// <summary>Gets the server that publishes the skill of a <c>skill</c>; <see langword="null"/> otherwise.</summary>
    public string? SkillServer { get; init; }

    /// <summary>Gets the name of the skill of a <c>skill</c>; <see langword="null"/> otherwise.</summary>
    public string? SkillName { get; init; }
}

/// <summary>What became of an answer to a permission request.</summary>
/// <param name="Status">
/// <list type="bullet">
/// <item><c>resolved</c>: the session has the answer.</item>
/// <item><c>rejected</c>: the request no longer waits: it was answered, or its turn ended.</item>
/// <item><c>uncertain</c>: the answer went, and the host does not know whether the session has it. It is not sent again.</item>
/// <item><c>invalid</c>: the request cannot be named so.</item>
/// <item><c>unavailable</c>: the host has no permission requests now (it starts, or it closes).</item>
/// </list>
/// </param>
public sealed record PluginPermissionOutcome(string Status)
{
    /// <summary>Gets whether the session has the answer.</summary>
    public bool Succeeded => Status == "resolved";
}

/// <summary>
/// The permission service of a host that knows which plugin asks. The runtime asks the host for the service of each plugin it
/// starts; a host that does not implement this interface gives no plugin permission requests. An answer allows what an agent
/// asked to do, so a host gives a working service only to the plugins of its own build that serve the user (the page of the
/// phones), never to a plugin of a folder, which an agent can write.
/// </summary>
public interface IPluginPermissionRuntimeService : IPluginPermissionService
{
    /// <summary>Gets the service of one plugin.</summary>
    /// <param name="pluginRuntimeKey">The runtime key of the plugin.</param>
    /// <param name="builtIn">Whether the plugin is one of the application's own build, not of a folder.</param>
    /// <returns>The service of that plugin: this one, or <see cref="NoopPluginPermissionService.Instance"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="pluginRuntimeKey"/> is null, empty or whitespace.</exception>
    IPluginPermissionService ForPlugin(string pluginRuntimeKey, bool builtIn);
}

/// <summary>The permission service of a host without it: no session waits, and nothing can be answered.</summary>
public sealed class NoopPluginPermissionService : IPluginPermissionService
{
    private NoopPluginPermissionService()
    {
    }

    /// <summary>Gets the shared instance.</summary>
    public static NoopPluginPermissionService Instance { get; } = new();

    /// <inheritdoc />
    public bool HasPermissions => false;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PluginPermissionSummary>> ListWaitingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<PluginPermissionSummary>>([]);

    /// <inheritdoc />
    public ValueTask<PluginPermissionPage> ListAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        return ValueTask.FromResult(new PluginPermissionPage([], false));
    }

    /// <inheritdoc />
    public ValueTask<PluginPermissionOutcome> ResolveAsync(string sessionId, string requestId, PluginPermissionDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(requestId);
        return ValueTask.FromResult(new PluginPermissionOutcome("unavailable"));
    }
}
