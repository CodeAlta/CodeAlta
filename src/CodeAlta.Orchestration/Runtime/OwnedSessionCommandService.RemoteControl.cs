using CodeAlta.Agent;
using CodeAlta.Catalog;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>The outcome of turning the remote control of an owned session on or off.</summary>
/// <param name="Status"><c>ok</c>; <c>unavailable</c> when the session is unknown or its provider has no remote control;
/// <c>busy</c> while its provider is being changed or it is being deleted; <c>closed</c> when the owner is closing.</param>
/// <param name="RemoteControl">The remote control of the session after the request, when <paramref name="Status"/> is <c>ok</c>.</param>
public sealed record OwnedRemoteControlResult(string Status, AgentRemoteControl? RemoteControl);

public sealed partial class OwnedSessionCommandService
{
    /// <summary>
    /// Turns the remote control of an owned session on or off (Claude Code's Remote Control). A session that is not
    /// attached is attached first, with what it runs with, as a send would attach it but without a run; the
    /// session is shown remotely under its title. Turning it off a session that is not attached changes nothing.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="enabled">Whether the session is to be controlled remotely.</param>
    /// <param name="cancellationToken">Cancels the request, not an attachment or a connection already made.</param>
    /// <returns>The outcome and, when it is <c>ok</c>, the remote control after the request.</returns>
    /// <exception cref="ArgumentException">The session identity is empty.</exception>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    public async Task<OwnedRemoteControlResult> SetRemoteControlAsync(string sessionId, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            if (_closed || _retained) return new("closed", null);
            // A provider that is being changed, or a session that is being deleted, is not attached meanwhile.
            if (_deleteWork is not null) return new("busy", null);
            // And none starts until the request ends: it would attach the session as it was before. A request
            // that turns it off while another turns it on is taken, as the provider takes it.
            _remoteControlling[sessionId] = _remoteControlling.GetValueOrDefault(sessionId) + 1;
        }

        try
        {
            var session = await _runtime.ResolveOwnedSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (session is null || !string.Equals(session.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return new("unavailable", null);
            var remoteControl = await _runtime.SetRemoteControlAsync(session.SessionId, enabled, RemoteName(session), cancellationToken).ConfigureAwait(false);
            if (remoteControl is null && enabled)
            {
                // Not attached: it is, with what the session runs with, and then turned on.
                var configured = SelectionProvider?.Invoke(new ModelProviderId(session.ResolvedProviderKey));
                if (configured is not { SupportsRemoteControl: true }) return new("unavailable", null);
                var project = string.IsNullOrWhiteSpace(session.ProjectRef) ? null
                    : await _projects.GetByIdAsync(session.ProjectRef, cancellationToken).ConfigureAwait(false);
                var (modelId, effort) = await CompleteModelAsync(session.ResolvedProviderKey, session.ModelId, session.ReasoningEffort,
                    observedOnly: false).ConfigureAwait(false);
                var policy = SessionExecutionPolicy.CaptureSession(session, project, _catalog.GlobalRoot, default, modelId, effort, session.AgentPromptId);
                var options = SessionExecutionPolicy.BuildOptions(
                    policy, ToolsFor(session.SessionId, project, project is null ? _catalog.GlobalRoot : WorkFolder(session, project), session.ResolvedProviderKey),
                    _runtime.Permissions.OwnedDefaultPermissionHandler,
                    _runtime.Permissions.OwnedDefaultUserInputHandler);
                // With what plugins give every run of the session, as a send has it: the next send keeps this
                // attachment, and the process whose bridge it connects.
                if (Plugins is not null)
                    options = await Plugins.AugmentAttachmentAsync(options, session.ProjectRef, session.SessionId, cancellationToken).ConfigureAwait(false);
                await _runtime.EnsureOwnedCoordinatorSessionAsync(session, options).ConfigureAwait(false);
                remoteControl = await _runtime.SetRemoteControlAsync(session.SessionId, enabled, RemoteName(session), cancellationToken).ConfigureAwait(false);
            }

            return remoteControl is null
                ? enabled ? new("unavailable", null) : new("ok", AgentRemoteControl.Off)
                : new("ok", remoteControl);
        }
        catch (ObjectDisposedException) { return new("closed", null); }
        finally
        {
            lock (_gate)
            {
                if (_remoteControlling[sessionId] == 1) _remoteControlling.Remove(sessionId);
                else _remoteControlling[sessionId]--;
            }
        }
    }

    // The name the session has remotely: its title, so that it is found in the Claude app under it.
    private static string? RemoteName(SessionViewDescriptor session)
    {
        var title = session.Title?.Trim();
        return string.IsNullOrEmpty(title) ? null : title.Length > 100 ? title[..100] : title;
    }
}
