using CodeAlta.Agent;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>The conversation of an owned session that other programs of its provider continue, or why there is none.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>unavailable</c> when the session is unknown; <c>unsupported</c> when its provider keeps no such
/// conversation; <c>none</c> when the session has none yet; <c>closed</c> when the owner is closing. A release adds its
/// refusals: <c>busy</c>, <c>running</c>, <c>background_tasks</c>, <c>remote_control</c>, <c>interaction_pending</c>,
/// <c>release_unconfirmed</c>.
/// </param>
/// <param name="Conversation">The conversation, when <paramref name="Status"/> is <c>ok</c>.</param>
public sealed record OwnedSharedConversationResult(string Status, OwnedSharedConversation? Conversation);

/// <summary>The conversation of an owned session that other programs of its provider continue.</summary>
/// <param name="ConversationId">The identifier the provider gives the conversation (the session of the Claude Code CLI).</param>
/// <param name="ResumeCommand">The command that continues it in a terminal opened in <paramref name="WorkingDirectory"/>.</param>
/// <param name="WorkingDirectory">The folder the conversation is resumed from: the one the session works in.</param>
/// <param name="EditorUri">
/// The address that opens it in the editor extension of the provider (VS Code), built by the provider from the recorded
/// identifier; null when it has none.
/// </param>
/// <param name="Holder">Another live program that holds the conversation now (the processes of CodeAlta are not), or null.</param>
public sealed record OwnedSharedConversation(string ConversationId, string ResumeCommand, string WorkingDirectory, string? EditorUri,
    AgentConversationHolder? Holder);

public sealed partial class OwnedSessionCommandService
{
    /// <summary>
    /// Describes the conversation the provider of an owned session keeps where other programs continue it (Claude Code:
    /// the conversation of the CLI, which VS Code and a terminal resume), and whether another program holds it now.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The conversation, or why there is none.</returns>
    /// <exception cref="ArgumentException">The session identity is empty.</exception>
    public async Task<OwnedSharedConversationResult> GetSharedConversationAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) { if (_closed || _retained) return new("closed", null); }
        try
        {
            var (status, conversation) = await _runtime.DescribeSharedConversationAsync(sessionId).ConfigureAwait(false);
            return new(status, conversation is null ? null
                : new(conversation.ConversationId, conversation.ResumeCommand, conversation.WorkingDirectory, conversation.EditorUri, conversation.Holder));
        }
        catch (ObjectDisposedException) { return new("closed", null); }
    }

    /// <summary>
    /// Gives the conversation of an idle owned session to another program: the process of the provider that CodeAlta
    /// keeps for the session after a turn is closed, so that the other program does not find the conversation held by
    /// CodeAlta. The next prompt sent here resumes the conversation as the other program left it.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>
    /// <c>ok</c> with the conversation once it is released; otherwise why it is not (see
    /// <see cref="OwnedSharedConversationResult.Status"/>): nothing was closed then.
    /// </returns>
    /// <exception cref="ArgumentException">The session identity is empty.</exception>
    public async Task<OwnedSharedConversationResult> ReleaseSharedConversationAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var described = await GetSharedConversationAsync(sessionId).ConfigureAwait(false);
        if (described.Status != "ok") return described;
        lock (_gate)
        {
            if (_closed || _retained) return new("closed", null);
            // A provider that is being changed, a session that is being deleted or moved, keeps what it does.
            if (Reserved(sessionId) || _deleteWork is not null || _remoteControlling.ContainsKey(sessionId)) return new("busy", null);
            if (Asks.List(sessionId).Head is not null) return new(SessionRuntimeService.ReleaseInteractionPending, null);
            if (_active.ContainsKey(sessionId) || _steering.Contains(sessionId) || _compacting.Contains(sessionId)
                || _abortingRuns.Contains(sessionId) || _queueing.Contains(sessionId)
                || _operations.Values.Any(operation => !operation.Released && operation.SessionId == sessionId)
                || _queues.Values.Any(operation => !operation.Released && operation.Request.SessionId == sessionId))
                return new(SessionRuntimeService.ReleaseRunning, null);
        }

        try
        {
            var released = await _runtime.ReleaseSharedConversationAsync(sessionId).ConfigureAwait(false);
            return released == SessionRuntimeService.ReleaseReleased ? described : new(released, null);
        }
        catch (ObjectDisposedException) { return new("closed", null); }
    }
}
