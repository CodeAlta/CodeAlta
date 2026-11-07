using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    /// <summary>
    /// Answers a side question about a session with its provider and its model, from the conversation its journal holds,
    /// while it runs or not. No CodeAlta session is created, attached or written, and the question and its answer are
    /// not added to its history. Provider-side retention follows the provider's own policy.
    /// </summary>
    /// <param name="sessionId">The session the question is about.</param>
    /// <param name="question">The question.</param>
    /// <param name="onText">Receives the text of the answer as it streams.</param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns>The whole answer.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> or <paramref name="question"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="onText"/> is null.</exception>
    /// <exception cref="KeyNotFoundException">No such session, or its provider is not configured.</exception>
    /// <exception cref="NotSupportedException">The provider of the session runs an agent of its own.</exception>
    /// <exception cref="InvalidOperationException">Neither the session nor its provider names a model.</exception>
    /// <exception cref="IOException">The journal cannot be read.</exception>
    /// <exception cref="ObjectDisposedException">The runtime has closed.</exception>
    /// <exception cref="OperationCanceledException">The request was stopped.</exception>
    public async Task<string> AskSideQuestionAsync(string sessionId, string question, Func<string, CancellationToken, ValueTask> onText,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(onText);
        return await AdmitAsync(() => AskSideQuestionBodyAsync(sessionId, question, onText, cancellationToken), cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> AskSideQuestionBodyAsync(string sessionId, string question, Func<string, CancellationToken, ValueTask> onText,
        CancellationToken cancellationToken)
    {
        var session = await TryGetResumableSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"No session '{sessionId}'.");
        var providerKey = string.IsNullOrWhiteSpace(session.ProviderKey) ? session.ProviderId : session.ProviderKey;
        if (string.IsNullOrWhiteSpace(providerKey)) throw new KeyNotFoundException($"The session '{sessionId}' names no provider.");
        Exception? historyError = null;
        var history = await TryReadStoredHistoryOwnedBodyAsync(session, ex => historyError = ex, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"The history of session '{sessionId}' could not be read.", historyError);
        var conversation = AgentSession.ReadConversation(history);
        var folder = string.IsNullOrWhiteSpace(session.WorktreeDirectory) ? session.WorkingDirectory : session.WorktreeDirectory;
        return await _agentHub.AskSideQuestionAsync(providerKey, session.ModelId, session.ReasoningEffort, folder, conversation, question,
            onText, cancellationToken).ConfigureAwait(false);
    }
}
