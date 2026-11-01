using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop;

/// <summary>
/// The permission requests the plugins of the window read and answer for the user, as the window does: through the same
/// service, which takes one answer for a request. Until the host is attached (while the plugins start), no session waits. Only
/// the built-in Mobile plugin gets them (<see cref="ForPlugin"/>): an answer allows what an agent asked to do, and a plugin of
/// a folder can be written by an agent.
/// </summary>
internal sealed class DesktopPluginPermissions : IPluginPermissionRuntimeService
{
    private Attached? _attached;

    /// <inheritdoc />
    public IPluginPermissionService ForPlugin(string pluginRuntimeKey, bool builtIn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRuntimeKey);
        return builtIn && pluginRuntimeKey == DesktopPluginAsks.MobileRuntimeKey ? this : NoopPluginPermissionService.Instance;
    }

    /// <summary>Gives the permission requests of the host once it runs.</summary>
    /// <param name="permissions">The service of the permission requests.</param>
    internal void Attach(SessionPermissionService permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        Attach(permissions.ListWaitingSessionsAsync, permissions.ListOwnedCommandsAsync, permissions.ResolveOwnedCommandAsync);
    }

    /// <summary>Gives the functions of the permission requests of the host.</summary>
    /// <param name="waiting">Lists the sessions that wait for the user.</param>
    /// <param name="list">Lists the requests of a session.</param>
    /// <param name="resolve">Answers a request.</param>
    internal void Attach(Func<ValueTask<IReadOnlyList<string>>> waiting, Func<string, CancellationToken, ValueTask<SessionOwnedPermissionPage>> list,
        Func<SessionOwnedPermissionHandle, AgentPermissionDecisionKind, CancellationToken, ValueTask<bool>> resolve)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(resolve);
        Volatile.Write(ref _attached, new(waiting, list, resolve));
    }

    /// <inheritdoc />
    public bool HasPermissions => Volatile.Read(ref _attached) is not null;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<PluginPermissionSummary>> ListWaitingAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _attached) is not { } attached) return [];
        var sessions = await attached.Waiting().ConfigureAwait(false);
        if (sessions.Count == 0) return [];
        var summaries = new List<PluginPermissionSummary>(sessions.Count);
        foreach (var sessionId in sessions)
        {
            // A session can wait for a form to fill, which is not a permission request.
            var page = await attached.List(sessionId, cancellationToken).ConfigureAwait(false);
            if (page.Entries.Count > 0) summaries.Add(new(sessionId, RequestIdOf(page.Entries[0]), page.Entries.Count));
        }

        return summaries;
    }

    /// <inheritdoc />
    public async ValueTask<PluginPermissionPage> ListAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (Volatile.Read(ref _attached) is not { } attached || !IsValidSessionId(sessionId)) return new([], false);
        var page = await attached.List(sessionId, cancellationToken).ConfigureAwait(false);
        return new([.. page.Entries.Select(ToPlugin)], page.HasMore);
    }

    /// <inheritdoc />
    public async ValueTask<PluginPermissionOutcome> ResolveAsync(string sessionId, string requestId, PluginPermissionDecision decision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(requestId);
        if (Volatile.Read(ref _attached) is not { } attached) return new("unavailable");
        if (!IsValidSessionId(sessionId) || !Guid.TryParse(requestId, out var attempt) || attempt == Guid.Empty) return new("invalid");
        var kind = decision switch
        {
            PluginPermissionDecision.AllowOnce => AgentPermissionDecisionKind.AllowOnce,
            PluginPermissionDecision.Deny => AgentPermissionDecisionKind.Deny,
            _ => (AgentPermissionDecisionKind?)null,
        };
        if (kind is null) return new("invalid");
        // The request is read again: an answer goes only to a request of this session that still waits.
        SessionOwnedPermissionSnapshot? entry;
        try
        {
            entry = (await attached.List(sessionId, cancellationToken).ConfigureAwait(false)).Entries.FirstOrDefault(candidate => candidate.Handle.Attempt.AttemptId == attempt);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new("unavailable");
        }

        if (entry is null) return new("rejected");
        try
        {
            return new(await attached.Resolve(entry.Handle, kind.Value, cancellationToken).ConfigureAwait(false) ? "resolved" : "rejected");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A failure is not proof that the answer did not go: it is not sent again.
            return new("uncertain");
        }
    }

    internal static PluginPermissionRequest ToPlugin(SessionOwnedPermissionSnapshot entry)
    {
        var request = entry.Request;
        return new()
        {
            RequestId = RequestIdOf(entry), Kind = request.Kind, Command = request.Command, WorkingDirectory = request.WorkingDirectory, GrantRoot = request.GrantRoot,
            Reason = request.Reason, Shortened = request.Shortened, OutsideSandbox = request.OutsideSandbox, SkillServer = request.Skill?.Server, SkillName = request.Skill?.Name,
        };
    }

    private static string RequestIdOf(SessionOwnedPermissionSnapshot entry) => entry.Handle.Attempt.AttemptId.ToString("D");

    private static bool IsValidSessionId(string sessionId) => OwnedSessionAskService.IsValidSessionId(sessionId);

    private sealed record Attached(Func<ValueTask<IReadOnlyList<string>>> Waiting, Func<string, CancellationToken, ValueTask<SessionOwnedPermissionPage>> List,
        Func<SessionOwnedPermissionHandle, AgentPermissionDecisionKind, CancellationToken, ValueTask<bool>> Resolve);
}
